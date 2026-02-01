using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Universal;

/// <summary>
/// UniversalAutoFight任务类，用于管理角色技能CD和出招表优先级
/// 采用预测式时间管理，使用全局时间而非每个角色独立时间
/// </summary>
public class UniversalAutoFightTask
{
    private readonly CombatScenes _combatScenes;
    private readonly UniversalAutoFightParser _parser;
    
    // 记录每个出招表的最后执行时间（角色名 + 出招表最大持续时间作为唯一标识）
    private Dictionary<string, double> _combatTableLastExecutionTime = new Dictionary<string, double>();
    
    // 技能CD信息字典（角色名 -> 技能CD列表）
    private Dictionary<string, List<SkillCooldownInfo>> _skillCooldowns = new Dictionary<string, List<SkillCooldownInfo>>();
    
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
    }
    
    /// <summary>
    /// 减少所有角色的所有技能CD
    /// </summary>
    /// <param name="timeElapsed">经过的时间</param>
    private void ReduceAllSkillCooldowns(double timeElapsed)
    {
        foreach (var avatarCooldowns in _skillCooldowns.Values)
        {
            for (int i = avatarCooldowns.Count - 1; i >= 0; i--)
            {
                avatarCooldowns[i].RemainingCooldown = Math.Max(0, avatarCooldowns[i].RemainingCooldown - timeElapsed);
                // 如果CD为0，移除以节省内存
                if (avatarCooldowns[i].RemainingCooldown <= 0)
                {
                    avatarCooldowns.RemoveAt(i);
                }
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
            
            // 计算所有出招表的真实优先级
            var priorityList = new List<(string AvatarName, UniversalAutoFightParser.CombatTableConfig Config, int Priority)>();
            
            foreach (var kvp in allCombatTables)
            {
                var avatarName = kvp.Key;
                var configs = kvp.Value;
                
                foreach (var config in configs)
                {
                    var realPriority = CalculateRealPriority(avatarName, config);
                    priorityList.Add((avatarName, config, realPriority));
                }
            }
            
            if (!priorityList.Any())
            {
                Logger.LogWarning("没有有效的出招表可供选择");
                return;
            }
            
            // 选择真实优先级最小的出招表
            var selected = priorityList.OrderBy(x => x.Priority).First();
            var selectedAvatarName = selected.AvatarName;
            var selectedConfig = selected.Config;
            var selectedPriority = selected.Priority;
            
            // 日志输出：当前正在使用的出招表
            Logger.LogInformation($"当前使用出招表: {selectedAvatarName} - 优先级: {selectedPriority}");
            
            // 日志输出：所有真实优先级小于10的出招表
            var highPriorityTables = priorityList.Where(x => x.Priority < 10).ToList();
            if (highPriorityTables.Any())
            {
                var highPriorityLog = string.Join(", ", highPriorityTables.Select(x => $"{x.AvatarName}(优先级:{x.Priority})"));
                Logger.LogInformation($"高优先级出招表(优先级<10): {highPriorityLog}");
            }
            
            // 执行选中的出招表
            TriggerCombatTable(selectedAvatarName, selectedConfig);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "万能自动战斗策略执行出错");
        }
    }

    /// <summary>
    /// 每帧更新（减少所有技能CD）
    /// </summary>
    /// <param name="deltaTime">自上一帧以来的时间（秒）</param>
    public void Update(double deltaTime)
    {
        // 减少所有技能CD
        ReduceAllSkillCooldowns(deltaTime);
    }
    
    /// <summary>
    /// 触发出招表执行
    /// </summary>
    /// <param name="avatarName">角色名</param>
    /// <param name="config">出招表配置</param>
    private void TriggerCombatTable(string avatarName, UniversalAutoFightParser.CombatTableConfig config)
    {

        
        // 2. 覆盖技能CD（仅限第一行声明的技能）- 这些是新设置的CD，不应该立即被减少
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
                // 覆盖现有的CD
                existingCd.RemainingCooldown = skillCd.CooldownTime;
            }
            else
            {
                // 添加新的CD
                _skillCooldowns[avatarName].Add(new SkillCooldownInfo(skillCd.SkillName, skillCd.CooldownTime));
            }
        }
        
        // 3. 更新所有出招表的累计执行时间（只针对动态优先级出招表）
        var tableKey = $"{avatarName}_{config.MaxDuration}";
        
        // 只有动态优先级出招表才需要维护累计时间
        if (config.PriorityConfig.Type == UniversalAutoFightParser.PriorityType.Dynamic)
        {
            // 为所有动态优先级出招表的累计时间增加当前出招表的持续时间
            foreach (var key in _combatTableLastExecutionTime.Keys.ToList())
            {
                _combatTableLastExecutionTime[key] += config.MaxDuration;
            }
            
            // 重置当前出招表的累计时间为0（因为刚执行完）
            _combatTableLastExecutionTime[tableKey] = 0;
        }
        else
        {
            // 静态优先级出招表不需要维护累计时间，确保不创建记录
            _combatTableLastExecutionTime.Remove(tableKey);
        }
        
        // 4. 减少所有角色的所有技能CD（全局时间流逝）
        // 恢复原始逻辑：新设置的CD也应该立即被减少
        ReduceAllSkillCooldowns(config.MaxDuration);
        
        // 5. 切换角色（如果需要）
        SwitchToAvatarIfNeeded(avatarName);
        
        // 6. 立即执行指令（不进行任何等待）
        ExecuteCommands(config.Commands, config.MaxDuration);
    }
    
    /// <summary>
    /// 减少所有角色的所有技能CD，但排除指定角色的指定技能（这些是新设置的CD）
    /// </summary>
    /// <param name="timeElapsed">经过的时间</param>
    /// <param name="excludeAvatarName">要排除的角色名</param>
    /// <param name="excludeSkillNames">要排除的技能名列表</param>
    private void ReduceAllSkillCooldownsExceptNewOnes(double timeElapsed, string excludeAvatarName, List<string> excludeSkillNames)
    {
        foreach (var kvp in _skillCooldowns)
        {
            var avatarName = kvp.Key;
            var avatarCooldowns = kvp.Value;
            
            for (int i = avatarCooldowns.Count - 1; i >= 0; i--)
            {
                var skillCd = avatarCooldowns[i];
                
                // 如果是新设置的技能CD，跳过减少
                if (avatarName == excludeAvatarName && excludeSkillNames.Contains(skillCd.SkillName))
                {
                    continue;
                }
                
                skillCd.RemainingCooldown = Math.Max(0, skillCd.RemainingCooldown - timeElapsed);
                // 如果CD为0，移除以节省内存
                if (skillCd.RemainingCooldown <= 0)
                {
                    avatarCooldowns.RemoveAt(i);
                }
            }
        }
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
    /// <param name="maxDuration">最大持续时间（秒）</param>
    private void ExecuteCommands(List<string> commands, double maxDuration)
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
            return;
        }
        
        // 创建执行器并执行指令
        var executor = new UniversalCombatCommandExecutor(currentAvatar, maxDuration, CancellationToken.None);
        executor.ExecuteCommands(commands);
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
    /// 计算出招表的真实优先级
    /// </summary>
    /// <param name="avatarName">角色名</param>
    /// <param name="config">出招表配置</param>
    /// <returns>真实优先级</returns>
    private int CalculateRealPriority(string avatarName, UniversalAutoFightParser.CombatTableConfig config)
    {
        // 规则3：当出招表内涉及的技能正在冷却中时，该出招表的真实优先级固定维持11
        bool hasSkillInCooldown = false;
        foreach (var skillCd in config.SkillCooldowns)
        {
            if (IsSkillOnCooldown(avatarName, skillCd.SkillName))
            {
                hasSkillInCooldown = true;
                break;
            }
        }
        
        if (hasSkillInCooldown)
        {
            return 11; // 固定优先级11，不可被减免
        }
        
        // 获取出招表特定的累计执行时间（角色名+最大持续时间作为唯一标识）
        var tableKey = $"{avatarName}_{config.MaxDuration}";
        double elapsedTime = _combatTableLastExecutionTime.TryGetValue(tableKey, out var lastTime) ? lastTime : 0;
        
        // 对于动态优先级出招表，检查是否需要清理内存
        if (config.PriorityConfig.Type == UniversalAutoFightParser.PriorityType.Dynamic)
        {
            // 如果累计时间已经超过EndTime，可以安全地移除记录以节省内存
            if (elapsedTime > config.PriorityConfig.EndTime)
            {
                _combatTableLastExecutionTime.Remove(tableKey);
                elapsedTime = config.PriorityConfig.EndTime; // 使用EndTime作为elapsedTime进行优先级计算
            }
        }
        
        // 获取基础优先级（使用出招表特定的累计时间来计算动态优先级）
        int basePriority = config.PriorityConfig.GetCurrentPriority(elapsedTime, elapsedTime > 0);
        
        // 规则2：当某个角色为前台角色时，他的所有出招表真实优先级减一
        try
        {
            var currentAvatarName = _combatScenes.CurrentAvatar();
            if (currentAvatarName != null && currentAvatarName == avatarName)
            {
                basePriority = Math.Max(1, basePriority - 1); // 确保优先级不低于1
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "获取当前出战角色失败");
        }
        
        // 规则1：真实优先级越小越优先（已经通过数值体现）
        return basePriority;
    }
    
    /// <summary>
    /// 更新全局冷却时间（应该在每次游戏循环中调用）
    /// </summary>
    /// <param name="deltaTime">经过的时间（秒）</param>

}