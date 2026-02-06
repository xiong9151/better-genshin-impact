using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Universal;

/// <summary>
/// UniversalAutoFight任务类，用于管理角色技能CD和出招表优先级
/// 采用预测式时间管理，基于出招表执行来推进虚拟时间，而非实时时间
/// CD管理完全依赖出招表的执行：每次执行出招表时，所有技能CD都会减少该出招表的持续时间
/// 支持特殊模式：当出招表第一行持续时间设置为0时，系统会使用实际执行时间进行CD计算和优先级更新
/// </summary>
public class UniversalAutoFightTask
{
    private readonly CombatScenes _combatScenes;
    private readonly UniversalAutoFightParser _parser;
    
    // 角色优先级配置（护盾排列、治疗排列、前台排列）
    private readonly Dictionary<string, List<string>> _rolePriorityConfig;
    
    // 记录每个动态优先级出招表的累计执行时间（角色名 + 出招表名称作为唯一标识）
    // 这个时间表示从上次执行完成到现在经过的虚拟时间
    private Dictionary<string, double> _combatTableLastExecutionTime = new Dictionary<string, double>();
    
    // 记录每个动态优先级出招表是否已经执行过（角色名 + 出招表名称作为唯一标识）
    private HashSet<string> _combatTableExecuted = new HashSet<string>();
    
    // 技能CD信息字典（角色名 -> 技能CD列表），包含所有已设置的技能CD，即使CD为0也会保留
    private Dictionary<string, List<SkillCooldownInfo>> _skillCooldowns = new Dictionary<string, List<SkillCooldownInfo>>();
    
    // 预处理的角色优先级映射（角色名 -> 优先级索引）
    private Dictionary<string, int> _shieldPriorityMap = new Dictionary<string, int>();
    private Dictionary<string, int> _healPriorityMap = new Dictionary<string, int>();
    private Dictionary<string, int> _frontlinePriorityMap = new Dictionary<string, int>();
    
    // 出招表解析缓存（队伍标识 -> 解析结果）
    private string _cachedTeamKey = null;
    private Dictionary<string, List<UniversalAutoFightParser.CombatTableConfig>> _cachedCombatTables = null;
    
    // 标记是否已经输出过队伍信息（仅在首次执行时输出）
    private bool _hasOutputTeamInfo = false;
    
    // 技能CD信息（基于预测时间）
    private class SkillCooldownInfo
    {
        public string SkillName { get; set; }
        public double RemainingCooldown { get; set; } // 剩余冷却时间
        
        public SkillCooldownInfo(string skillName, double cooldownTime)
        {
            SkillName = skillName;
            RemainingCooldown = cooldownTime;
        }
    }
    
    public UniversalAutoFightTask(CombatScenes combatScenes)
    {
        _combatScenes = combatScenes;
        _skillCooldowns = new Dictionary<string, List<SkillCooldownInfo>>();
        _parser = new UniversalAutoFightParser();
        
        // 读取角色优先级配置
        _rolePriorityConfig = _parser.ReadRolePriorityConfig() ?? new Dictionary<string, List<string>>();
        
        // 预处理角色优先级映射
        PreprocessRolePriorityMaps();
        
        // 添加调试日志
        if (_rolePriorityConfig != null && _rolePriorityConfig.Count > 0)
        {
            Logger.LogInformation($"成功加载角色优先级配置，包含 {string.Join(", ", _rolePriorityConfig.Keys)}");
            if (_rolePriorityConfig.TryGetValue("护盾排列", out var shieldList))
            {
                Logger.LogInformation($"护盾排列包含 {shieldList.Count} 个角色: [{string.Join(", ", shieldList.Take(5))}{(shieldList.Count > 5 ? "..." : "")}]");
            }
            if (_rolePriorityConfig.TryGetValue("治疗排列", out var healList))
            {
                Logger.LogInformation($"治疗排列包含 {healList.Count} 个角色: [{string.Join(", ", healList.Take(5))}{(healList.Count > 5 ? "..." : "")}]");
            }
            if (_rolePriorityConfig.TryGetValue("前台排列", out var frontlineList))
            {
                Logger.LogInformation($"前台排列包含 {frontlineList.Count} 个角色: [{string.Join(", ", frontlineList.Take(5))}{(frontlineList.Count > 5 ? "..." : "")}]");
            }
        }
        else
        {
            Logger.LogWarning("角色优先级配置为空或未加载");
        }
    }
    
    /// <summary>
    /// 预处理角色优先级映射，将角色名映射到优先级索引
    /// </summary>
    private void PreprocessRolePriorityMaps()
    {
        _shieldPriorityMap.Clear();
        _healPriorityMap.Clear();
        _frontlinePriorityMap.Clear();
        
        if (_rolePriorityConfig.TryGetValue("护盾排列", out var shieldList))
        {
            for (int i = 0; i < shieldList.Count; i++)
            {
                _shieldPriorityMap[shieldList[i]] = i;
            }
        }
        
        if (_rolePriorityConfig.TryGetValue("治疗排列", out var healList))
        {
            for (int i = 0; i < healList.Count; i++)
            {
                _healPriorityMap[healList[i]] = i;
            }
        }
        
        if (_rolePriorityConfig.TryGetValue("前台排列", out var frontlineList))
        {
            for (int i = 0; i < frontlineList.Count; i++)
            {
                _frontlinePriorityMap[frontlineList[i]] = i;
            }
        }
    }
    
    /// <summary>
    /// 获取角色在指定排列中的优先级索引（0表示最高优先级）
    /// </summary>
    /// <param name="priorityMap">预处理的优先级映射</param>
    /// <param name="avatarName">角色名称</param>
    /// <returns>优先级索引，如果不在列表中返回-1</returns>
    private int GetRolePriorityIndexFromMap(Dictionary<string, int> priorityMap, string avatarName)
    {
        if (string.IsNullOrEmpty(avatarName))
            return -1;
            
        return priorityMap.TryGetValue(avatarName, out var index) ? index : -1;
    }
    
    /// <summary>
    /// 判断角色是否为护盾角色
    /// </summary>
    /// <param name="avatarName">角色名称</param>
    /// <returns>是否为护盾角色</returns>
    private bool IsShieldRole(string avatarName)
    {
        return _shieldPriorityMap.ContainsKey(avatarName);
    }
    
    /// <summary>
    /// 判断角色是否为治疗角色
    /// </summary>
    /// <param name="avatarName">角色名称</param>
    /// <returns>是否为治疗角色</returns>
    private bool IsHealRole(string avatarName)
    {
        return _healPriorityMap.ContainsKey(avatarName);
    }
    
    /// <summary>
    /// 获取角色在前台排列中的优先级索引
    /// </summary>
    /// <param name="avatarName">角色名称</param>
    /// <returns>前台优先级索引，如果不在列表中返回一个较大的值</returns>
    private int GetFrontlinePriorityIndex(string avatarName)
    {
        var index = GetRolePriorityIndexFromMap(_frontlinePriorityMap, avatarName);
        return index >= 0 ? index : int.MaxValue;
    }
    
    /// <summary>
    /// 减少所有角色的所有技能CD（基于出招表执行的虚拟时间流逝）
    /// 注意：即使CD减少到0，也会保留技能记录，以确保CD检查逻辑能正确识别技能状态
    /// </summary>
    /// <param name="timeElapsed">经过的虚拟时间（通常为出招表的持续时间）</param>
    private void ReduceAllSkillCooldowns(double timeElapsed)
    {
        foreach (var avatarCooldowns in _skillCooldowns.Values)
        {
            for (int i = avatarCooldowns.Count - 1; i >= 0; i--)
            {
                avatarCooldowns[i].RemainingCooldown = Math.Max(0, avatarCooldowns[i].RemainingCooldown - timeElapsed);
                // 不再移除CD为0的技能，以确保CD检查逻辑正确工作
                // if (avatarCooldowns[i].RemainingCooldown <= 0)
                // {
                //     avatarCooldowns.RemoveAt(i);
                // }
            }
        }
    }
    
    /// <summary>
    /// 核心处理方法，选择并执行出招表
    /// </summary>
    public void ProcessAutoFight()
    {
        try
        {
            var avatars = _combatScenes.GetAvatars().ToList();
            if (avatars == null || !avatars.Any())
            {
                Logger.LogWarning("未获取到队伍角色信息");
                return;
            }
            
            // 获取当前队伍角色名称列表（复用avatars结果）
            var teamAvatarNames = avatars.Select(a => a.Name).ToList();
            
            // 找出当前队伍中在护盾排列中最靠前的角色
            string highestPriorityShieldRole = null;
            int minShieldIndex = int.MaxValue;
            foreach (var avatarName in teamAvatarNames)
            {
                var shieldIndex = GetRolePriorityIndexFromMap(_shieldPriorityMap, avatarName);
                if (shieldIndex >= 0 && shieldIndex < minShieldIndex)
                {
                    minShieldIndex = shieldIndex;
                    highestPriorityShieldRole = avatarName;
                }
            }
            
            // 找出当前队伍中在治疗排列中最靠前的角色
            string highestPriorityHealRole = null;
            int minHealIndex = int.MaxValue;
            foreach (var avatarName in teamAvatarNames)
            {
                var healIndex = GetRolePriorityIndexFromMap(_healPriorityMap, avatarName);
                if (healIndex >= 0 && healIndex < minHealIndex)
                {
                    minHealIndex = healIndex;
                    highestPriorityHealRole = avatarName;
                }
            }
            
            // 获取前台排列顺序
            List<string> frontlineOrder = new List<string>();
            if (_rolePriorityConfig.TryGetValue("前台排列", out var frontlineList))
            {
                // 使用HashSet优化Contains操作
                var teamAvatarSet = new HashSet<string>(teamAvatarNames);
                // 只显示当前队伍中的角色在前台排列中的顺序
                frontlineOrder = frontlineList.Where(role => teamAvatarSet.Contains(role)).ToList();
            }
            else
            {
                frontlineOrder = teamAvatarNames.ToList(); // 如果没有前台排列配置，使用队伍顺序
            }
            
            // 输出队伍信息（只在第一次执行时输出，避免重复）
            if (!_hasOutputTeamInfo)
            {
                Logger.LogInformation($"=== 万能自动战斗开始 ===");
                Logger.LogInformation($"队伍角色: [{string.Join(", ", teamAvatarNames)}]");
                Logger.LogInformation($"盾位最优先: {(highestPriorityShieldRole ?? "无")}");
                Logger.LogInformation($"治疗位最优先: {(highestPriorityHealRole ?? "无")}");
                Logger.LogInformation($"前台顺序: [{string.Join(" > ", frontlineOrder)}]");
                _hasOutputTeamInfo = true;
            }

            // 获取所有出招表配置（带缓存）
            var teamKey = string.Join(",", teamAvatarNames.OrderBy(x => x));
            Dictionary<string, List<UniversalAutoFightParser.CombatTableConfig>> allCombatTables;
            
            if (_cachedTeamKey == teamKey && _cachedCombatTables != null)
            {
                // 使用缓存
                allCombatTables = _cachedCombatTables;
            }
            else
            {
                // 重新解析并缓存
                allCombatTables = _parser.ParseCombatTables(teamAvatarNames);
                _cachedTeamKey = teamKey;
                _cachedCombatTables = allCombatTables;
            }
            
            // 计算所有出招表的真实优先级，并记录详细的计算过程
            var priorityList = new List<(string AvatarName, UniversalAutoFightParser.CombatTableConfig Config, int Priority, string CalculationDetails)>();
            
            foreach (var kvp in allCombatTables)
            {
                var avatarName = kvp.Key;
                var configs = kvp.Value;
                
                foreach (var config in configs)
                {
                    var (realPriority, calculationDetails) = CalculateRealPriorityWithDetails(avatarName, config, highestPriorityShieldRole, highestPriorityHealRole);
                    priorityList.Add((avatarName, config, realPriority, calculationDetails));
                }
            }
            
            if (!priorityList.Any())
            {
                Logger.LogWarning("没有有效的出招表可供选择");
                return;
            }
            
            // 按优先级排序
            var sortedPriorityList = priorityList.OrderBy(x => x.Priority).ToList();
            
            // 找到最小优先级
            var minPriority = sortedPriorityList.First().Priority;
            var candidates = sortedPriorityList.Where(x => x.Priority == minPriority).ToList();
            
            // 如果只有一个候选，直接选择
            if (candidates.Count == 1)
            {
                var selected = candidates[0];
                var combatTableName = !string.IsNullOrEmpty(selected.Config.CombatTableName) ? 
                    $"({selected.Config.CombatTableName})" : "";
                Logger.LogInformation($"选择出招表: {selected.AvatarName}{combatTableName}");
                
                // 执行选中的出招表
                var actualDuration = TriggerCombatTable(selected.AvatarName, selected.Config);
                var presetDuration = selected.Config.MaxDuration;
                if (presetDuration <= 0)
                {
                    Logger.LogInformation($"出招表执行完成: 指令耗时 {actualDuration:F2}s");
                }
                else
                {
                    Logger.LogInformation($"出招表执行完成: 预设 {presetDuration:F2}s, 指令 {actualDuration:F2}s");
                }
                return;
            }
            
            // 处理多个相同优先级的情况
            // 1. 如果其中有护盾角色，则护盾角色优先执行
            (string AvatarName, UniversalAutoFightParser.CombatTableConfig Config, int Priority, string CalculationDetails)? selectedCandidate = null;
            
            // 优先检查护盾角色
            foreach (var candidate in candidates)
            {
                if (IsShieldRole(candidate.AvatarName))
                {
                    if (selectedCandidate == null || GetFrontlinePriorityIndex(candidate.AvatarName) < GetFrontlinePriorityIndex(selectedCandidate.Value.AvatarName))
                    {
                        selectedCandidate = candidate;
                    }
                }
            }
            
            if (selectedCandidate != null)
            {
                var selected = selectedCandidate.Value;
                var combatTableName = !string.IsNullOrEmpty(selected.Config.CombatTableName) ? 
                    $"({selected.Config.CombatTableName})" : "";
                Logger.LogInformation($"选择出招表: {selected.AvatarName}{combatTableName} (护盾角色优先)");
                var actualDuration = TriggerCombatTable(selected.AvatarName, selected.Config);
                var presetDuration = selected.Config.MaxDuration;
                if (presetDuration <= 0)
                {
                    Logger.LogInformation($"出招表执行完成: 指令耗时 {actualDuration:F2}s");
                }
                else
                {
                    Logger.LogInformation($"出招表执行完成: 预设 {presetDuration:F2}s, 指令 {actualDuration:F2}s");
                }
                return;
            }
            
            // 2. 其次如果其中不存在护盾角色，但存在治疗角色，则治疗角色优先
            foreach (var candidate in candidates)
            {
                if (IsHealRole(candidate.AvatarName))
                {
                    if (selectedCandidate == null || GetFrontlinePriorityIndex(candidate.AvatarName) < GetFrontlinePriorityIndex(selectedCandidate.Value.AvatarName))
                    {
                        selectedCandidate = candidate;
                    }
                }
            }
            
            if (selectedCandidate != null)
            {
                var selected = selectedCandidate.Value;
                var combatTableName = !string.IsNullOrEmpty(selected.Config.CombatTableName) ? 
                    $"({selected.Config.CombatTableName})" : "";
                Logger.LogInformation($"选择出招表: {selected.AvatarName}{combatTableName} (治疗角色优先)");
                var actualDuration = TriggerCombatTable(selected.AvatarName, selected.Config);
                var presetDuration = selected.Config.MaxDuration;
                if (presetDuration <= 0)
                {
                    Logger.LogInformation($"出招表执行完成: 指令耗时 {actualDuration:F2}s");
                }
                else
                {
                    Logger.LogInformation($"出招表执行完成: 预设 {presetDuration:F2}s, 指令 {actualDuration:F2}s");
                }
                return;
            }
            
            // 3. 如果既没有护盾也没有治疗角色，则按前台排列顺序选择
            foreach (var candidate in candidates)
            {
                if (selectedCandidate == null || GetFrontlinePriorityIndex(candidate.AvatarName) < GetFrontlinePriorityIndex(selectedCandidate.Value.AvatarName))
                {
                    selectedCandidate = candidate;
                }
            }
            
            if (selectedCandidate != null)
            {
                var selected = selectedCandidate.Value;
                var combatTableName = !string.IsNullOrEmpty(selected.Config.CombatTableName) ? 
                    $"({selected.Config.CombatTableName})" : "";
                Logger.LogInformation($"选择出招表: {selected.AvatarName}{combatTableName} (前台顺序)");
                var actualDuration = TriggerCombatTable(selected.AvatarName, selected.Config);
                var presetDuration = selected.Config.MaxDuration;
                if (presetDuration <= 0)
                {
                    Logger.LogInformation($"出招表执行完成: 指令耗时 {actualDuration:F2}s");
                }
                else
                {
                    Logger.LogInformation($"出招表执行完成: 预设 {presetDuration:F2}s, 指令 {actualDuration:F2}s");
                }
                return;
            }
            
            // 按照出招表名称排序（字母顺序）
            var sameAvatarCandidates = candidates.Where(c => c.AvatarName == candidates.First().AvatarName).ToList();
            if (sameAvatarCandidates.Count > 1)
            {
                // 按出招表名称排序
                var selected = sameAvatarCandidates.OrderBy(c => c.Config.CombatTableName ?? "").First();
                var combatTableName = !string.IsNullOrEmpty(selected.Config.CombatTableName) ? 
                    $"({selected.Config.CombatTableName})" : "";
                Logger.LogInformation($"选择出招表: {selected.AvatarName}{combatTableName} (出招表名称优先)");
                var actualDuration = TriggerCombatTable(selected.AvatarName, selected.Config);
                var presetDuration = selected.Config.MaxDuration;
                if (presetDuration <= 0)
                {
                    Logger.LogInformation($"出招表执行完成: 指令耗时 {actualDuration:F2}s");
                }
                else
                {
                    Logger.LogInformation($"出招表执行完成: 预设 {presetDuration:F2}s, 指令 {actualDuration:F2}s");
                }
                return;
            }
            
            // 最后的兜底逻辑：选择第一个
            var finalSelected = candidates.First();
            var finalCombatTableName = !string.IsNullOrEmpty(finalSelected.Config.CombatTableName) ? 
                $"({finalSelected.Config.CombatTableName})" : "";
            Logger.LogInformation($"选择出招表: {finalSelected.AvatarName}{finalCombatTableName} (兜底选择)");
            var finalActualDuration = TriggerCombatTable(finalSelected.AvatarName, finalSelected.Config);
            var finalPresetDuration = finalSelected.Config.MaxDuration;
            if (finalPresetDuration <= 0)
            {
                Logger.LogInformation($"出招表执行完成: 指令耗时 {finalActualDuration:F2}s");
            }
            else
            {
                Logger.LogInformation($"出招表执行完成: 预设 {finalPresetDuration:F2}s, 指令 {finalActualDuration:F2}s");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "万能自动战斗策略执行出错");
        }
    }
    
    /// <summary>
    /// 触发出招表执行
    /// </summary>
    /// <param name="avatarName">角色名</param>
    /// <param name="config">出招表配置</param>
    /// <returns>实际执行时间（秒）</returns>
    private double TriggerCombatTable(string avatarName, UniversalAutoFightParser.CombatTableConfig config)
    {
        // 切换角色（如果需要）
        SwitchToAvatarIfNeeded(avatarName);
        
        double actualDuration = 0;
        
        // 检查是否为持续时间为0的特殊情况
        if (config.MaxDuration <= 0)
        {
            // 持续时间为0：执行指令后获取实际执行时间，再进行CD设置和时间更新
            actualDuration = ExecuteCommands(config.Commands, 0);
            
            // 如果实际执行时间为0，使用一个很小的时间值避免除零错误
            actualDuration = Math.Max(actualDuration, 0.01);
            
            // 1. 设置技能CD（仅限第一行声明的技能）
           // 将出招表中定义的技能CD设置为完整值，覆盖已存在的CD记录
            if (!_skillCooldowns.ContainsKey(avatarName))
            {
                _skillCooldowns[avatarName] = new List<SkillCooldownInfo>();
            }
            
            foreach (var skillCd in config.SkillCooldowns)
            {
                // 查找是否已存在该技能的CD
                var existingCd = _skillCooldowns[avatarName].FirstOrDefault(cd => cd.SkillName == skillCd.SkillName);
                if (existingCd != null)
                {
                    // 覆盖现有的CD，确保CD值不小于0
                    existingCd.RemainingCooldown = Math.Max(0, skillCd.CooldownTime);
                }
                else
                {
                    // 添加新的CD，确保CD值不小于0
                    _skillCooldowns[avatarName].Add(new SkillCooldownInfo(skillCd.SkillName, Math.Max(0, skillCd.CooldownTime)));
                }
            }
            
            // 2. 更新动态优先级出招表的累计执行时间（使用实际执行时间）
            var tableKey = $"{avatarName}_{config.CombatTableName}";
            
            // 为所有已执行的动态优先级出招表的累计时间增加当前出招表的实际执行时间
            // 注意：这是全局时间更新，任意出招表执行都会影响所有动态优先级的时间
            foreach (var key in _combatTableLastExecutionTime.Keys.ToList())
            {
                // 只有已执行的出招表才累积时间
                if (_combatTableExecuted.Contains(key))
                {
                    _combatTableLastExecutionTime[key] += actualDuration;
                }
            }
            
            // 如果当前出招表是动态优先级出招表，需要特殊处理
            if (config.PriorityConfig.Type == UniversalAutoFightParser.PriorityType.Dynamic)
            {
                // 确保当前出招表有时间记录
                if (!_combatTableLastExecutionTime.ContainsKey(tableKey))
                {
                    _combatTableLastExecutionTime[tableKey] = 0;
                }
                
                // 标记当前出招表为已执行
                _combatTableExecuted.Add(tableKey);
                
                // 重置当前出招表的累计时间为0（因为刚执行完，重新开始计时）
                _combatTableLastExecutionTime[tableKey] = 0;
            }
            
            // 3. 减少所有角色的所有技能CD（基于实际执行时间）
            // 所有技能CD都会减少当前出招表的实际执行时间，包括刚刚设置的新CD
            ReduceAllSkillCooldowns(actualDuration);
        }
        else
        {
            // 持续时间 > 0：正常处理流程
            
            // 1. 设置技能CD（仅限第一行声明的技能）
            // 将出招表中定义的技能CD设置为完整值，覆盖已存在的CD记录
            if (!_skillCooldowns.ContainsKey(avatarName))
            {
                _skillCooldowns[avatarName] = new List<SkillCooldownInfo>();
            }
            
            foreach (var skillCd in config.SkillCooldowns)
            {
                // 查找是否已存在该技能的CD
                var existingCd = _skillCooldowns[avatarName].FirstOrDefault(cd => cd.SkillName == skillCd.SkillName);
                if (existingCd != null)
                {
                    // 覆盖现有的CD，确保CD值不小于0
                    existingCd.RemainingCooldown = Math.Max(0, skillCd.CooldownTime);
                }
                else
                {
                    // 添加新的CD，确保CD值不小于0
                    _skillCooldowns[avatarName].Add(new SkillCooldownInfo(skillCd.SkillName, Math.Max(0, skillCd.CooldownTime)));
                }
            }
            
            // 2. 更新动态优先级出招表的累计执行时间
            var tableKey = $"{avatarName}_{config.CombatTableName}";
            
            // 为所有已执行的动态优先级出招表的累计时间增加当前出招表的持续时间
            // 注意：这是全局时间更新，任意出招表执行都会影响所有动态优先级的时间
            foreach (var key in _combatTableLastExecutionTime.Keys.ToList())
            {
                // 只有已执行的出招表才累积时间
                if (_combatTableExecuted.Contains(key))
                {
                    _combatTableLastExecutionTime[key] += config.MaxDuration;
                }
            }
            
            // 如果当前出招表是动态优先级出招表，需要特殊处理
            if (config.PriorityConfig.Type == UniversalAutoFightParser.PriorityType.Dynamic)
            {
                // 确保当前出招表有时间记录
                if (!_combatTableLastExecutionTime.ContainsKey(tableKey))
                {
                    _combatTableLastExecutionTime[tableKey] = 0;
                }
                
                // 标记当前出招表为已执行
                _combatTableExecuted.Add(tableKey);
                
                // 重置当前出招表的累计时间为0（因为刚执行完，重新开始计时）
                _combatTableLastExecutionTime[tableKey] = 0;
            }
            
            // 3. 减少所有角色的所有技能CD（基于出招表持续时间）
            // 所有技能CD都会减少当前出招表的持续时间，包括刚刚设置的新CD
            ReduceAllSkillCooldowns(config.MaxDuration);
            
            // 4. 执行指令（会自动添加wait补齐不足的时间）
            actualDuration = ExecuteCommands(config.Commands, config.MaxDuration);
        }
        
        return actualDuration;
    }
    
    /// <summary>
    /// 如果当前出战角色不是目标角色，则切换到目标角色
    /// </summary>
    /// <param name="targetAvatarName">目标角色名称</param>
    private void SwitchToAvatarIfNeeded(string targetAvatarName)
    {
        try
        {
            // 获取当前出战角色
            var currentAvatarName = _combatScenes.CurrentAvatar();
            
            // 如果当前角色就是目标角色，无需切换
            if (currentAvatarName != null && currentAvatarName == targetAvatarName)
            {
                return;
            }
            
            // 查找目标角色
            var targetAvatar = _combatScenes.GetAvatars().FirstOrDefault(a => a.Name == targetAvatarName);
            if (targetAvatar == null)
            {
                Logger.LogWarning("未找到目标角色: {TargetAvatarName}", targetAvatarName);
                return;
            }
            
            // 切换到目标角色
            Logger.LogInformation("切换角色: {Current} -> {Target}", currentAvatarName ?? "未知", targetAvatarName);
            targetAvatar.SwitchWithoutCts();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "切换角色失败: {TargetAvatarName}", targetAvatarName);
        }
    }
    
    /// <summary>
    /// 执行指令列表
    /// </summary>
    /// <param name="commands">指令列表</param>
    /// <param name="maxDuration">最大持续时间（秒），如果为0则使用实际执行时间</param>
    /// <returns>用户指令的实际执行时间（秒，不包括自动添加的wait时间）</returns>
    private double ExecuteCommands(List<string> commands, double maxDuration)
    {
        // 获取当前出战角色用于执行指令
        Avatar? currentAvatar = null;
        try
        {
            var currentAvatarName = _combatScenes.CurrentAvatar();
            if (!string.IsNullOrEmpty(currentAvatarName))
            {
                var avatar = _combatScenes.GetAvatars().FirstOrDefault(a => a.Name == currentAvatarName);
                if (avatar != null)
                {
                    currentAvatar = avatar;
                }
            }
            
            // 如果无法获取当前角色，使用第一个角色
            if (currentAvatar == null)
            {
                currentAvatar = _combatScenes.GetAvatars().FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "获取执行角色失败，使用默认角色");
            currentAvatar = _combatScenes.GetAvatars().FirstOrDefault();
        }
        
        if (currentAvatar == null)
        {
            Logger.LogWarning("无法获取有效的执行角色，跳过指令执行");
            return 0;
        }
        
        // 如果maxDuration为0，只执行原始指令，返回实际执行时间
        if (maxDuration <= 0)
        {
            var executor = new UniversalCombatCommandExecutor(currentAvatar, 0, CancellationToken.None);
            var actualDuration = executor.ExecuteCommandsAndGetDuration(commands);
            return actualDuration;
        }
        else
        {
            // 执行原始指令并获取实际执行时间（不包括自动添加的wait）
            var executor = new UniversalCombatCommandExecutor(currentAvatar, maxDuration, CancellationToken.None);
            var actualDuration = executor.ExecuteCommandsAndGetDuration(commands);
            
            // 如果实际执行时间超过maxDuration，说明指令被截断了，不需要添加wait
            // 如果实际执行时间小于maxDuration，需要添加wait指令补齐
            if (actualDuration < maxDuration)
            {
                var waitTime = maxDuration - actualDuration;
                if (waitTime > 0.01) // 避免微小的等待时间
                {
                    Logger.LogDebug($"指令执行时间({actualDuration:F2}s)不足最大持续时间({maxDuration:F2}s)，添加wait({waitTime:F2})补齐");
                    // 执行wait指令
                    var waitCommands = new List<string> { $"wait({waitTime:F2})" };
                    var waitExecutor = new UniversalCombatCommandExecutor(currentAvatar, waitTime, CancellationToken.None);
                    waitExecutor.ExecuteCommands(waitCommands);
                }
            }
            // 如果 actualDuration >= maxDuration，说明已经执行了完整时间或被截断，不需要添加wait
            
            // 返回用户指令的实际执行时间（不包括自动添加的wait）
            return actualDuration;
        }
    }
    
    /// <summary>
    /// 检查技能是否在冷却中
    /// </summary>
    /// <param name="avatarName">角色名</param>
    /// <param name="skillName">技能名</param>
    /// <returns>是否在冷却中</returns>
    private bool IsSkillOnCooldown(string avatarName, string skillName)
    {
        if (!_skillCooldowns.ContainsKey(avatarName))
        {
            return false;
        }
        
        var skillCd = _skillCooldowns[avatarName].FirstOrDefault(cd => cd.SkillName == skillName);
        return skillCd != null && skillCd.RemainingCooldown > 0;
    }
    
    /// <summary>
    /// 获取技能的实际剩余冷却时间
    /// </summary>
    /// <param name="avatarName">角色名</param>
    /// <param name="skillName">技能名</param>
    /// <returns>剩余冷却时间，如果技能不在冷却中则返回0</returns>
    private double GetSkillRemainingCooldown(string avatarName, string skillName)
    {
        if (!_skillCooldowns.ContainsKey(avatarName))
        {
            return 0;
        }
        
        var skillCd = _skillCooldowns[avatarName].FirstOrDefault(cd => cd.SkillName == skillName);
        return skillCd?.RemainingCooldown ?? 0;
    }
    
    /// <summary>
    /// 计算出招表的真实优先级并返回详细的计算过程
    /// </summary>
    /// <param name="avatarName">角色名</param>
    /// <param name="config">出招表配置</param>
    /// <param name="highestPriorityShieldRole">当前队伍中护盾排列最靠前的角色</param>
    /// <param name="highestPriorityHealRole">当前队伍中治疗排列最靠前的角色</param>
    /// <returns>真实优先级和计算详情</returns>
    private (int Priority, string CalculationDetails) CalculateRealPriorityWithDetails(string avatarName, UniversalAutoFightParser.CombatTableConfig config, string highestPriorityShieldRole, string highestPriorityHealRole)
    {
        var calculationSteps = new List<string>();
        
        // 规则：当出招表内涉及的技能正在冷却中，且剩余CD时间大于接受的CD时间时，
        // 该出招表的真实优先级固定维持11（不可被减免）
        bool shouldFixPriorityTo11 = false;
        foreach (var skillCd in config.SkillCooldowns)
        {
            if (IsSkillOnCooldown(avatarName, skillCd.SkillName))
            {
                // 获取技能的实际剩余冷却时间
                var actualRemainingCooldown = GetSkillRemainingCooldown(avatarName, skillCd.SkillName);
                // 如果实际CD时间大于接受的CD时间，则触发优先级11
                if (actualRemainingCooldown > skillCd.AcceptedCooldownTime)
                {
                    shouldFixPriorityTo11 = true;
                    break;
                }
            }
        }
        
        if (shouldFixPriorityTo11)
        {
            calculationSteps.Add("技能冷却超过接受值");
            return (11, $"优先级11 ({string.Join(", ", calculationSteps)})"); // 固定优先级11，不可被减免
        }
        
        // 获取出招表特定的累计执行时间（角色名+出招表名称作为唯一标识）
        var tableKey = $"{avatarName}_{config.CombatTableName}";
        double elapsedTime = _combatTableLastExecutionTime.TryGetValue(tableKey, out var lastTime) ? lastTime : 0;
        
        // 检查出招表是否已经执行过
        bool isExecuted = _combatTableExecuted.Contains(tableKey);
        
        // 对于动态优先级出招表，检查是否需要清理
        if (config.PriorityConfig.Type == UniversalAutoFightParser.PriorityType.Dynamic)
        {
            // 如果累计时间已经超过EndTime，清除执行标记并重置时间
            if (elapsedTime > config.PriorityConfig.EndTime)
            {
                // 清除执行标记
                _combatTableExecuted.Remove(tableKey);
                isExecuted = false;
                
                // 重置累计时间为0，表示回到初始状态
                _combatTableLastExecutionTime[tableKey] = 0;
                elapsedTime = 0;
            }
        }
        
        // 获取基础优先级（使用出招表特定的累计时间和执行状态来计算动态优先级）
        var (basePriority, priorityCalculationDetails) = config.PriorityConfig.GetCurrentPriorityWithDetails(elapsedTime, isExecuted);
        calculationSteps.Add(priorityCalculationDetails);
        
        // 规则2：当某个角色为前台角色时，他的所有出招表真实优先级减一
        try
        {
            var currentAvatarName = _combatScenes.CurrentAvatar();
            if (currentAvatarName != null && currentAvatarName == avatarName)
            {
                basePriority = Math.Max(1, basePriority - 1); // 确保优先级不低于1
                calculationSteps.Add("前台角色减1");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "获取当前出战角色失败");
        }
        
        // 新增规则：在当前队伍中，护盾排列最靠前的角色优先级-1（即更优先）
        if (highestPriorityShieldRole != null && avatarName == highestPriorityShieldRole)
        {
            basePriority = Math.Max(1, basePriority - 1);
            calculationSteps.Add("盾位减1");
        }
        
        // 新增规则：在当前队伍中，治疗排列最靠前的角色优先级也减一
        if (highestPriorityHealRole != null && avatarName == highestPriorityHealRole)
        {
            basePriority = Math.Max(1, basePriority - 1);
            calculationSteps.Add("治疗减1");
        }
        
        // 规则1：真实优先级越小越优先（已经通过数值体现）
        return (basePriority, $"优先级{basePriority} ({string.Join(", ", calculationSteps)})");
    }
    

}