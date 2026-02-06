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
    private Dictionary<string, double> _combatTableLastExecutionTime = new Dictionary<string, double>();
    
    // 记录每个动态优先级出招表是否已经执行过（角色名 + 出招表名称作为唯一标识）
    private HashSet<string> _combatTableExecuted = new HashSet<string>();
    
    // 技能CD信息字典（角色名 -> 技能CD列表），包含所有已设置的技能CD，即使CD为0也会保留
    private Dictionary<string, List<SkillCooldownInfo>> _skillCooldowns = new Dictionary<string, List<SkillCooldownInfo>>();
    
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
    /// 获取角色在指定排列中的优先级索引（0表示最高优先级）
    /// </summary>
    /// <param name="roleList">角色列表</param>
    /// <param name="avatarName">角色名称</param>
    /// <returns>优先级索引，如果不在列表中返回-1</returns>
    private int GetRolePriorityIndex(List<string> roleList, string avatarName)
    {
        if (roleList == null || string.IsNullOrEmpty(avatarName))
            return -1;
            
        for (int i = 0; i < roleList.Count; i++)
        {
            if (roleList[i] == avatarName)
                return i;
        }
        return -1;
    }
    
    /// <summary>
    /// 判断角色是否为护盾角色
    /// </summary>
    /// <param name="avatarName">角色名称</param>
    /// <returns>是否为护盾角色</returns>
    private bool IsShieldRole(string avatarName)
    {
        if (_rolePriorityConfig.TryGetValue("护盾排列", out var shieldList))
        {
            return shieldList.Contains(avatarName);
        }
        return false;
    }
    
    /// <summary>
    /// 判断角色是否为治疗角色
    /// </summary>
    /// <param name="avatarName">角色名称</param>
    /// <returns>是否为治疗角色</returns>
    private bool IsHealRole(string avatarName)
    {
        if (_rolePriorityConfig.TryGetValue("治疗排列", out var healList))
        {
            return healList.Contains(avatarName);
        }
        return false;
    }
    
    /// <summary>
    /// 获取角色在前台排列中的优先级索引
    /// </summary>
    /// <param name="avatarName">角色名称</param>
    /// <returns>前台优先级索引，如果不在列表中返回一个较大的值</returns>
    private int GetFrontlinePriorityIndex(string avatarName)
    {
        if (_rolePriorityConfig.TryGetValue("前台排列", out var frontlineList))
        {
            var index = GetRolePriorityIndex(frontlineList, avatarName);
            return index >= 0 ? index : int.MaxValue;
        }
        return int.MaxValue;
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
            
            var teamAvatarNames = avatars.Select(a => a.Name).ToList();
            var allCombatTables = _parser.ParseCombatTables(teamAvatarNames);
            
            if (!allCombatTables.Any())
            {
                Logger.LogWarning("未找到任何出招表配置");
                return;
            }
            
            // 找出当前队伍中在护盾排列中最靠前的角色
            string highestPriorityShieldRole = null;
            int minShieldIndex = int.MaxValue;
            if (_rolePriorityConfig.TryGetValue("护盾排列", out var shieldList))
            {
                Logger.LogDebug($"护盾排列列表长度: {shieldList.Count}");
                foreach (var avatarName in teamAvatarNames)
                {
                    var shieldIndex = GetRolePriorityIndex(shieldList, avatarName);
                    Logger.LogDebug($"角色 {avatarName} 在护盾排列中的索引: {shieldIndex}");
                    if (shieldIndex >= 0 && shieldIndex < minShieldIndex)
                    {
                        minShieldIndex = shieldIndex;
                        highestPriorityShieldRole = avatarName;
                    }
                }
            }
            
            // 找出当前队伍中在治疗排列中最靠前的角色
            string highestPriorityHealRole = null;
            int minHealIndex = int.MaxValue;
            if (_rolePriorityConfig.TryGetValue("治疗排列", out var healList))
            {
                Logger.LogDebug($"治疗排列列表长度: {healList.Count}");
                foreach (var avatarName in teamAvatarNames)
                {
                    var healIndex = GetRolePriorityIndex(healList, avatarName);
                    Logger.LogDebug($"角色 {avatarName} 在治疗排列中的索引: {healIndex}");
                    if (healIndex >= 0 && healIndex < minHealIndex)
                    {
                        minHealIndex = healIndex;
                        highestPriorityHealRole = avatarName;
                    }
                }
            }
            
            // 获取前台排列顺序
            var frontlineOrder = new List<string>();
            if (_rolePriorityConfig.TryGetValue("前台排列", out var frontlineList))
            {
                Logger.LogDebug($"前台排列列表长度: {frontlineList.Count}");
                // 只显示当前队伍中的角色在前台排列中的顺序
                frontlineOrder = frontlineList.Where(role => teamAvatarNames.Contains(role)).ToList();
                Logger.LogDebug($"前台排列中找到的队伍角色数量: {frontlineOrder.Count}");
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
            
            // 按优先级排序，输出前三个
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
                Logger.LogInformation($"详细计算: {selected.CalculationDetails}");
                
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
            var shieldCandidates = candidates.Where(c => IsShieldRole(c.AvatarName)).ToList();
            if (shieldCandidates.Any())
            {
                // 在护盾角色中选择前台排列最靠前的
                var selected = shieldCandidates.OrderBy(c => GetFrontlinePriorityIndex(c.AvatarName)).First();
                var combatTableName = !string.IsNullOrEmpty(selected.Config.CombatTableName) ? 
                    $"({selected.Config.CombatTableName})" : "";
                Logger.LogInformation($"选择出招表: {selected.AvatarName}{combatTableName} (护盾角色优先)");
                Logger.LogInformation($"详细计算: {selected.CalculationDetails}");
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
            var healCandidates = candidates.Where(c => IsHealRole(c.AvatarName)).ToList();
            if (healCandidates.Any())
            {
                // 在治疗角色中选择前台排列最靠前的
                var selected = healCandidates.OrderBy(c => GetFrontlinePriorityIndex(c.AvatarName)).First();
                var combatTableName = !string.IsNullOrEmpty(selected.Config.CombatTableName) ? 
                    $"({selected.Config.CombatTableName})" : "";
                Logger.LogInformation($"选择出招表: {selected.AvatarName}{combatTableName} (治疗角色优先)");
                Logger.LogInformation($"详细计算: {selected.CalculationDetails}");
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
            var frontlineCandidates = candidates.OrderBy(c => GetFrontlinePriorityIndex(c.AvatarName)).ToList();
            if (frontlineCandidates.Any())
            {
                var selected = frontlineCandidates.First();
                var combatTableName = !string.IsNullOrEmpty(selected.Config.CombatTableName) ? 
                    $"({selected.Config.CombatTableName})" : "";
                Logger.LogInformation($"选择出招表: {selected.AvatarName}{combatTableName} (前台顺序)");
                Logger.LogInformation($"详细计算: {selected.CalculationDetails}");
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
                Logger.LogInformation($"详细计算: {selected.CalculationDetails}");
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
            Logger.LogInformation($"详细计算: {finalSelected.CalculationDetails}");
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
            
            // 只有动态优先级出招表才需要维护累计时间和执行状态
            if (config.PriorityConfig.Type == UniversalAutoFightParser.PriorityType.Dynamic)
            {
                // 为所有其他动态优先级出招表的累计时间增加当前出招表的实际执行时间
                foreach (var key in _combatTableLastExecutionTime.Keys.ToList())
                {
                    if (key != tableKey) // 不包括当前出招表
                    {
                        _combatTableLastExecutionTime[key] += actualDuration;
                    }
                }
                
                // 重置当前出招表的累计时间为0（因为刚执行完，从现在开始重新计时）
                _combatTableLastExecutionTime[tableKey] = 0;
                
                // 标记当前出招表为已执行
                _combatTableExecuted.Add(tableKey);
            }
            else
            {
                // 静态优先级出招表不需要维护累计时间，确保不创建记录
                _combatTableLastExecutionTime.Remove(tableKey);
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
            
            // 只有动态优先级出招表才需要维护累计时间和执行状态
            if (config.PriorityConfig.Type == UniversalAutoFightParser.PriorityType.Dynamic)
            {
                // 为所有其他动态优先级出招表的累计时间增加当前出招表的持续时间
                foreach (var key in _combatTableLastExecutionTime.Keys.ToList())
                {
                    if (key != tableKey) // 不包括当前出招表
                    {
                        _combatTableLastExecutionTime[key] += config.MaxDuration;
                    }
                }
                
                // 重置当前出招表的累计时间为0（因为刚执行完，从现在开始重新计时）
                _combatTableLastExecutionTime[tableKey] = 0;
                
                // 标记当前出招表为已执行
                _combatTableExecuted.Add(tableKey);
            }
            else
            {
                // 静态优先级出招表不需要维护累计时间，确保不创建记录
                _combatTableLastExecutionTime.Remove(tableKey);
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
        
        // 对于动态优先级出招表，检查是否需要清理内存
        if (config.PriorityConfig.Type == UniversalAutoFightParser.PriorityType.Dynamic)
        {
            // 如果累计时间已经超过EndTime，可以安全地移除记录以节省内存
            if (elapsedTime > config.PriorityConfig.EndTime)
            {
                _combatTableLastExecutionTime.Remove(tableKey);
                _combatTableExecuted.Remove(tableKey); // 同时清理执行标志
                elapsedTime = config.PriorityConfig.EndTime; // 使用EndTime作为elapsedTime进行优先级计算
                isExecuted = true; // 仍然认为已执行过，用于正确计算优先级
            }
        }
        
        // 获取基础优先级（使用出招表特定的累计时间和执行状态来计算动态优先级）
        int basePriority = config.PriorityConfig.GetCurrentPriority(elapsedTime, isExecuted);
        calculationSteps.Add($"出招表优先级{basePriority}");
        
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
    
    /// <summary>
    /// 计算出招表的真实优先级（旧版本，保持兼容性）
    /// </summary>
    /// <param name="avatarName">角色名</param>
    /// <param name="config">出招表配置</param>
    /// <param name="highestPriorityShieldRole">当前队伍中护盾排列最靠前的角色</param>
    /// <param name="highestPriorityHealRole">当前队伍中治疗排列最靠前的角色</param>
    /// <returns>真实优先级</returns>
    private int CalculateRealPriority(string avatarName, UniversalAutoFightParser.CombatTableConfig config, string highestPriorityShieldRole, string highestPriorityHealRole)
    {
        var (priority, _) = CalculateRealPriorityWithDetails(avatarName, config, highestPriorityShieldRole, highestPriorityHealRole);
        return priority;
    }
    
    /// <summary>
    /// 更新全局冷却时间（应该在每次游戏循环中调用）
    /// </summary>
    /// <param name="deltaTime">经过的时间（秒）</param>

}