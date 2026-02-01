using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight.Universal;

public class UniversalAutoFightParser
{
    /// <summary>
    /// 优先级类型
    /// </summary>
    public enum PriorityType
    {
        Static,   // 静态优先级
        Dynamic   // 动态优先级
    }

    /// <summary>
    /// 优先级配置
    /// </summary>
    public class PriorityConfig
    {
        public PriorityType Type { get; set; }
        
        // 静态优先级值
        public int StaticPriority { get; set; }
        
        // 动态优先级参数
        public double StartTime { get; set; } = 0;
        public double EndTime { get; set; } = 0;
        public int StartPriority { get; set; } = 0;
        public int EndPriority { get; set; } = 0;
        public int DefaultPriority { get; set; } = 1; // 未执行时和其他时间的默认优先级
        
        public PriorityConfig(int staticPriority)
        {
            Type = PriorityType.Static;
            StaticPriority = staticPriority;
            DefaultPriority = staticPriority; // 修复：静态优先级的默认值应该等于静态优先级本身
        }
        
        public PriorityConfig(double startTime, double endTime, int startPriority, int endPriority, int defaultPriority = 1)
        {
            Type = PriorityType.Dynamic;
            StartTime = startTime;
            EndTime = endTime;
            StartPriority = startPriority;
            EndPriority = endPriority;
            DefaultPriority = defaultPriority;
        }
        
        /// <summary>
        /// 计算当前优先级
        /// </summary>
        /// <param name="elapsedTime">执行后经过的时间（秒）</param>
        /// <param name="isExecuted">是否已执行</param>
        /// <returns>当前优先级数值</returns>
        public int GetCurrentPriority(double elapsedTime, bool isExecuted)
        {
            if (!isExecuted)
            {
                return DefaultPriority; // 未执行时使用常态优先级（动态优先级的第5个值，或静态优先级的值）
            }
            
            if (Type == PriorityType.Static)
            {
                return StaticPriority;
            }
            else // Dynamic
            {
                if (elapsedTime >= StartTime && elapsedTime <= EndTime)
                {
                    // 线性插值计算优先级
                    var ratio = (elapsedTime - StartTime) / (EndTime - StartTime);
                    var priority = StartPriority + (EndPriority - StartPriority) * ratio;
                    return (int)Math.Round(priority);
                }
                else
                {
                    return DefaultPriority; // 不在指定时间段内时使用常态优先级（动态优先级的第5个值）
                }
            }
        }
    }

    /// <summary>
    /// 技能CD信息
    /// </summary>
    public class SkillCooldown
    {
        public string SkillName { get; set; }
        public double CooldownTime { get; set; }
        
        public SkillCooldown(string skillName, double cooldownTime)
        {
            SkillName = skillName;
            CooldownTime = cooldownTime;
        }
    }

    /// <summary>
    /// 出招表配置
    /// </summary>
    public class CombatTableConfig
    {
        public double MaxDuration { get; set; }
        public List<SkillCooldown> SkillCooldowns { get; set; } = new List<SkillCooldown>();
        public PriorityConfig PriorityConfig { get; set; }
        public List<string> Commands { get; set; } = new List<string>(); // 指令列表
        
        public CombatTableConfig(double maxDuration)
        {
            MaxDuration = maxDuration;
        }
    }

    /// <summary>
    /// 根据队伍角色查找并解析对应的出招表文件
    /// </summary>
    /// <param name="teamAvatars">队伍中的角色列表</param>
    /// <returns>角色名到出招表配置列表的映射</returns>
    public Dictionary<string, List<CombatTableConfig>> ParseCombatTables(List<string> teamAvatars)
    {
        var result = new Dictionary<string, List<CombatTableConfig>>();
        var universalPath = Path.Combine("User", "UniversalAutoFight");
        
        if (!Directory.Exists(universalPath))
        {
            Logger.LogWarning("UniversalAutoFight目录不存在: {Path}", universalPath);
            return result;
        }

        foreach (var avatarName in teamAvatars)
        {
            var avatarDir = Path.Combine(universalPath, avatarName);
            if (!Directory.Exists(avatarDir))
            {
                Logger.LogWarning("角色目录不存在: {Path}", avatarDir);
                continue;
            }

            // 查找出招表_*.txt文件
            var combatTableFiles = Directory.GetFiles(avatarDir, "出招表_*.txt", SearchOption.TopDirectoryOnly);
            if (combatTableFiles.Length == 0)
            {
                Logger.LogWarning("未找到出招表文件: {Path}", avatarDir);
                continue;
            }

            var configs = new List<CombatTableConfig>();
            foreach (var configFile in combatTableFiles)
            {
                try
                {
                    var config = ParseCombatTableFile(configFile);
                    if (config != null)
                    {
                        configs.Add(config);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "解析出招表文件失败: {Path}", configFile);
                }
            }
            
            if (configs.Count > 0)
            {
                result[avatarName] = configs;
            }
        }

        return result;
    }

    /// <summary>
    /// 解析单个出招表文件
    /// </summary>
    /// <param name="filePath">出招表文件路径</param>
    /// <returns>出招表配置</returns>
    private CombatTableConfig ParseCombatTableFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        if (lines.Length == 0)
        {
            Logger.LogWarning("出招表文件为空: {Path}", filePath);
            return null;
        }

        var firstLine = lines[0].Trim();
        if (string.IsNullOrEmpty(firstLine))
        {
            Logger.LogWarning("出招表第一行为空: {Path}", filePath);
            return null;
        }

        // 解析格式：<最大持续时间> <技能名>:<CD时间>[;<技能名>:<CD时间>]*
        var parts = firstLine.Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            Logger.LogError("出招表格式错误，应为：<最大持续时间> <技能名>:<CD时间>[;<技能名>:<CD时间>]*");
            throw new FormatException("出招表格式错误");
        }

        if (!double.TryParse(parts[0], out var maxDuration) || maxDuration <= 0)
        {
            Logger.LogError("最大持续时间必须是正数: {Value}", parts[0]);
            throw new FormatException("最大持续时间格式错误");
        }

        var config = new CombatTableConfig(maxDuration);
        var skillParts = parts[1].Split(';');
        
        foreach (var skillPart in skillParts)
        {
            var skillCooldown = ParseSkillCooldown(skillPart);
            if (skillCooldown != null)
            {
                config.SkillCooldowns.Add(skillCooldown);
            }
        }

        // 解析第二行优先级设置（如果存在）
        if (lines.Length >= 2)
        {
            var priorityLine = lines[1].Trim();
            if (!string.IsNullOrEmpty(priorityLine))
            {
                config.PriorityConfig = ParsePriorityConfig(priorityLine);
            }
        }

        // 如果没有设置优先级，默认使用静态优先级10
        if (config.PriorityConfig == null)
        {
            config.PriorityConfig = new PriorityConfig(10);
        }

        // 解析第3行及之后的指令行
        for (int i = 2; i < lines.Length; i++)
        {
            var commandLine = lines[i].Trim();
            if (!string.IsNullOrEmpty(commandLine) && !commandLine.StartsWith("//") && !commandLine.StartsWith("#"))
            {
                config.Commands.Add(commandLine);
            }
        }

        return config;
    }

    /// <summary>
    /// 解析优先级配置
    /// </summary>
    /// <param name="priorityLine">优先级行内容</param>
    /// <returns>优先级配置</returns>
    private PriorityConfig ParsePriorityConfig(string priorityLine)
    {
        // 检查是否为动态优先级（包含多个连字符）
        var dashCount = priorityLine.Count(c => c == '-');
        if (dashCount >= 3)
        {
            // 动态优先级：时间1-时间2-优先级a-优先级b-优先级c
            var parts = priorityLine.Split('-');
            if (parts.Length >= 5)
            {
                if (double.TryParse(parts[0], out var startTime) &&
                    double.TryParse(parts[1], out var endTime) &&
                    int.TryParse(parts[2], out var startPriority) &&
                    int.TryParse(parts[3], out var endPriority) &&
                    int.TryParse(parts[4], out var defaultPriority))
                {
                    return new PriorityConfig(startTime, endTime, startPriority, endPriority, defaultPriority);
                }
            }
            // 尝试4个部分（没有默认优先级，默认为1）
            else if (parts.Length >= 4)
            {
                if (double.TryParse(parts[0], out var startTime) &&
                    double.TryParse(parts[1], out var endTime) &&
                    int.TryParse(parts[2], out var startPriority) &&
                    int.TryParse(parts[3], out var endPriority))
                {
                    return new PriorityConfig(startTime, endTime, startPriority, endPriority, 1);
                }
            }
        }
        else
        {
            // 静态优先级：<优先级c>
            if (int.TryParse(priorityLine, out var staticPriority))
            {
                return new PriorityConfig(staticPriority);
            }
        }

        Logger.LogWarning("优先级格式无法识别，默认使用优先级1: {Line}", priorityLine);
        return new PriorityConfig(1);
    }

    /// <summary>
    /// 解析单个技能CD配置
    /// </summary>
    /// <param name="skillPart">技能部分字符串，格式为"技能名:CD时间"</param>
    /// <returns>技能CD信息</returns>
    private SkillCooldown ParseSkillCooldown(string skillPart)
    {
        var trimmedPart = skillPart.Trim();
        if (string.IsNullOrEmpty(trimmedPart))
            return null;

        var colonIndex = trimmedPart.IndexOf(':');
        if (colonIndex == -1)
        {
            Logger.LogWarning("技能CD格式错误，缺少冒号: {Part}", trimmedPart);
            return null;
        }

        var skillName = trimmedPart.Substring(0, colonIndex).Trim();
        var cooldownStr = trimmedPart.Substring(colonIndex + 1).Trim();

        if (string.IsNullOrEmpty(skillName) || string.IsNullOrEmpty(cooldownStr))
        {
            Logger.LogWarning("技能CD格式错误，技能名或CD时间为空: {Part}", trimmedPart);
            return null;
        }

        if (!double.TryParse(cooldownStr, out var cooldownTime) || cooldownTime <= 0)
        {
            Logger.LogWarning("技能CD时间必须是正数: {Cooldown}", cooldownStr);
            return null;
        }

        return new SkillCooldown(skillName, cooldownTime);
    }
}