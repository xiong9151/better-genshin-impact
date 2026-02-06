using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
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
            if (Type == PriorityType.Static)
            {
                return StaticPriority;
            }
            else // Dynamic
            {
                if (!isExecuted)
                {
                    return DefaultPriority; // 未执行时使用默认优先级
                }
                else if (elapsedTime >= StartTime && elapsedTime <= EndTime)
                {
                    // 线性插值计算优先级
                    var ratio = (elapsedTime - StartTime) / (EndTime - StartTime);
                    var priority = StartPriority + (EndPriority - StartPriority) * ratio;
                    return (int)Math.Round(priority);
                }
                else
                {
                    return DefaultPriority; // 超出时间范围后也使用默认优先级
                }
            }
        }
        
        /// <summary>
        /// 获取当前优先级值和详细的计算过程
        /// </summary>
        /// <param name="elapsedTime">已执行时间（秒）</param>
        /// <param name="isExecuted">是否已经执行过</param>
        /// <returns>优先级值和计算详情</returns>
        public (int Priority, string CalculationDetails) GetCurrentPriorityWithDetails(double elapsedTime, bool isExecuted)
        {
            if (Type == PriorityType.Static)
            {
                return (StaticPriority, $"静态优先级: {StaticPriority}");
            }
            else // Dynamic
            {
                if (!isExecuted)
                {
                    return (DefaultPriority, $"动态优先级: 未执行, 使用默认优先级 {DefaultPriority}");
                }
                else if (elapsedTime < StartTime)
                {
                    // 在 [0, StartTime) 区间，使用默认优先级
                    return (DefaultPriority, $"动态优先级: 执行时间 {elapsedTime:F2}s < 起始时间 {StartTime:F2}s, 使用默认优先级 {DefaultPriority}");
                }
                else if (elapsedTime >= StartTime && elapsedTime <= EndTime)
                {
                    // 在 [StartTime, EndTime] 区间，使用插值计算
                    var ratio = (elapsedTime - StartTime) / (EndTime - StartTime);
                    var priority = StartPriority + (EndPriority - StartPriority) * ratio;
                    var roundedPriority = (int)Math.Round(priority);
                    var details = $"动态优先级: 执行时间 {elapsedTime:F2}s, 时间范围 [{StartTime:F2}-{EndTime:F2}]s, " +
                                 $"优先级范围 [{StartPriority}-{EndPriority}], " +
                                 $"插值比例 {ratio:F3}, 计算值 {priority:F2}, 四舍五入 {roundedPriority}";
                    return (roundedPriority, details);
                }
                else
                {
                    // 在 (EndTime, ∞) 区间，使用默认优先级
                    return (DefaultPriority, $"动态优先级: 执行时间 {elapsedTime:F2}s > 结束时间 {EndTime:F2}s, 使用默认优先级 {DefaultPriority}");
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
        public double AcceptedCooldownTime { get; set; } // 接受的CD时间，如果技能CD小于此值，则不会触发优先级11
        
        public SkillCooldown(string skillName, double cooldownTime, double acceptedCooldownTime = 0)
        {
            SkillName = skillName;
            CooldownTime = cooldownTime;
            AcceptedCooldownTime = acceptedCooldownTime;
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
        public string CombatTableName { get; set; } // 出招表标识名称（从文件名中提取）
        
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

        // 解析格式：<最大持续时间> <技能名>:<CD时间>[:<接受的CD时间>][;<技能名>:<CD时间>[:<接受的CD时间>]]*
        // 接受的CD时间指的是如果对应的技能CD小于接受的CD时间时该优先级不会被固定为11，
        // 只有当对应技能的真实CD大于接受的CD时间时，该出招表的优先级才会被固定为11，
        // 接受的CD时间这一项可以省略，若省略则默认为零。
        var parts = firstLine.Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !double.TryParse(parts[0], out var maxDuration) || maxDuration < 0)
        {
            Logger.LogError("出招表格式错误，第一行必须包含有效的最大持续时间: {Value}", firstLine);
            throw new FormatException("出招表格式错误");
        }

        var config = new CombatTableConfig(maxDuration);
        
        // 从文件路径中提取出招表标识名称（出招表_***.txt 中的 *** 部分）
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (fileName.StartsWith("出招表_"))
        {
            config.CombatTableName = fileName.Substring("出招表_".Length);
        }
        else
        {
            config.CombatTableName = fileName; // 如果不符合命名规范，使用完整文件名
        }

        // 解析技能冷却信息（如果存在）
        if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]))
        {
            var skillParts = parts[1].Split(';');
            
            foreach (var skillPart in skillParts)
            {
                var skillCooldown = ParseSkillCooldown(skillPart);
                if (skillCooldown != null)
                {
                    config.SkillCooldowns.Add(skillCooldown);
                }
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
                    // 验证优先级范围 (1-10)
                    if (startPriority < 1 || startPriority > 10 ||
                        endPriority < 1 || endPriority > 10 ||
                        defaultPriority < 1 || defaultPriority > 10)
                    {
                        Logger.LogWarning("动态优先级值超出范围(1-10)，使用默认优先级10: {Line}", priorityLine);
                        return new PriorityConfig(10);
                    }
                    // 验证时间范围
                    if (startTime > endTime)
                    {
                        Logger.LogWarning("动态优先级时间范围错误（时间1 > 时间2），使用默认优先级10: {Line}", priorityLine);
                        return new PriorityConfig(10);
                    }
                    return new PriorityConfig(startTime, endTime, startPriority, endPriority, defaultPriority);
                }
            }
            // 尝试4个部分（没有默认优先级，默认为10）
            else if (parts.Length >= 4)
            {
                if (double.TryParse(parts[0], out var startTime) &&
                    double.TryParse(parts[1], out var endTime) &&
                    int.TryParse(parts[2], out var startPriority) &&
                    int.TryParse(parts[3], out var endPriority))
                {
                    // 验证优先级范围 (1-10)
                    if (startPriority < 1 || startPriority > 10 ||
                        endPriority < 1 || endPriority > 10)
                    {
                        Logger.LogWarning("动态优先级值超出范围(1-10)，使用默认优先级10: {Line}", priorityLine);
                        return new PriorityConfig(10);
                    }
                    // 验证时间范围
                    if (startTime > endTime)
                    {
                        Logger.LogWarning("动态优先级时间范围错误（时间1 > 时间2），使用默认优先级10: {Line}", priorityLine);
                        return new PriorityConfig(10);
                    }
                    return new PriorityConfig(startTime, endTime, startPriority, endPriority, 10);
                }
            }
        }
        else
        {
            // 静态优先级：<优先级c>
            if (int.TryParse(priorityLine, out var staticPriority))
            {
                // 验证优先级范围 (1-10)
                if (staticPriority < 1 || staticPriority > 10)
                {
                    Logger.LogWarning("静态优先级值超出范围(1-10)，使用默认优先级10: {Line}", priorityLine);
                    return new PriorityConfig(10);
                }
                return new PriorityConfig(staticPriority);
            }
        }

        Logger.LogWarning("优先级格式无法识别，默认使用优先级10: {Line}", priorityLine);
        return new PriorityConfig(10);
    }

    /// <summary>
    /// 解析单个技能CD配置
    /// </summary>
    /// <param name="skillPart">技能部分字符串，格式为"技能名:CD时间"或"技能名:CD时间:接受的CD时间"</param>
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
        var remainingPart = trimmedPart.Substring(colonIndex + 1).Trim();

        if (string.IsNullOrEmpty(skillName) || string.IsNullOrEmpty(remainingPart))
        {
            Logger.LogWarning("技能CD格式错误，技能名或CD时间为空: {Part}", trimmedPart);
            return null;
        }

        // 支持两种格式：CD时间 或 CD时间:接受的CD时间
        var timeParts = remainingPart.Split(':');
        if (timeParts.Length == 1)
        {
            // 格式：技能名:CD时间
            if (!double.TryParse(timeParts[0], out var cooldownTime) || cooldownTime < 0)
            {
                Logger.LogWarning("技能CD时间必须是非负数: {Cooldown}", timeParts[0]);
                return null;
            }
            return new SkillCooldown(skillName, cooldownTime, 0); // 默认接受的CD时间为0
        }
        else if (timeParts.Length == 2)
        {
            // 格式：技能名:CD时间:接受的CD时间
            if (!double.TryParse(timeParts[0], out var cooldownTime) || cooldownTime < 0)
            {
                Logger.LogWarning("技能CD时间必须是非负数: {Cooldown}", timeParts[0]);
                return null;
            }
            if (!double.TryParse(timeParts[1], out var acceptedCooldownTime) || acceptedCooldownTime < 0)
            {
                Logger.LogWarning("接受的CD时间必须是非负数: {AcceptedCooldown}", timeParts[1]);
                return null;
            }
            return new SkillCooldown(skillName, cooldownTime, acceptedCooldownTime);
        }
        else
        {
            Logger.LogWarning("技能CD格式错误，最多只能有两个冒号: {Part}", trimmedPart);
            return null;
        }
    }

    /// <summary>
    /// 读取角色优先级配置文件
    /// </summary>
    /// <returns>角色优先级配置字典，如果文件不存在或读取失败则返回null</returns>
    public Dictionary<string, List<string>> ReadRolePriorityConfig()
    {
        var universalPath = Path.Combine("User", "UniversalAutoFight");
        var priorityFile = Path.Combine(universalPath, "角色优先级.yaml");
        
        if (!File.Exists(priorityFile))
        {
            Logger.LogWarning("角色优先级配置文件不存在: {Path}", priorityFile);
            return null;
        }

        try
        {
            var yamlContent = File.ReadAllText(priorityFile);
            var deserializer = new DeserializerBuilder().Build();
            var config = deserializer.Deserialize<Dictionary<string, List<string>>>(yamlContent);
            
            Logger.LogInformation("成功读取角色优先级配置文件: {Path}", priorityFile);
            return config;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "读取角色优先级配置文件失败: {Path}", priorityFile);
            return null;
        }
    }
}