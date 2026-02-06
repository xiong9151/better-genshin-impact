using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight.Universal;

/// <summary>
/// Universal出招表指令执行器
/// 使用与原有战斗策略相同的执行方法
/// </summary>
public class UniversalCombatCommandExecutor
{
    private readonly Avatar _avatar;
    private readonly double _maxDuration;
    private readonly CancellationToken _cancellationToken;
    private DateTime _startTime;
    private DateTime _maxEndTime;

    public UniversalCombatCommandExecutor(Avatar avatar, double maxDuration, CancellationToken cancellationToken)
    {
        _avatar = avatar;
        _maxDuration = maxDuration;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// 执行出招表中的所有指令
    /// </summary>
    /// <param name="commands">指令列表</param>
    public void ExecuteCommands(List<string> commands)
    {
        _startTime = DateTime.Now;
        // 如果_maxDuration为0，不设置超时限制
        _maxEndTime = _maxDuration > 0 ? _startTime.AddSeconds(_maxDuration) : DateTime.MaxValue;

        foreach (var commandLine in commands)
        {
            if (_cancellationToken.IsCancellationRequested)
                return;

            // 检查是否超过最大持续时间（仅当_maxDuration > 0时）
            if (_maxDuration > 0 && DateTime.Now >= _maxEndTime)
            {
                Logger.LogInformation("出招表执行超时，停止执行剩余指令");
                break;
            }

            ExecuteCommandLine(commandLine);
        }
    }

    /// <summary>
    /// 执行出招表中的所有指令并返回实际执行时间
    /// </summary>
    /// <param name="commands">指令列表</param>
    /// <returns>实际执行时间（秒）</returns>
    public double ExecuteCommandsAndGetDuration(List<string> commands)
    {
        var startTime = DateTime.Now;
        _startTime = startTime;
        // 如果_maxDuration为0，不设置超时限制
        _maxEndTime = _maxDuration > 0 ? startTime.AddSeconds(_maxDuration) : DateTime.MaxValue;

        foreach (var commandLine in commands)
        {
            if (_cancellationToken.IsCancellationRequested)
                return (DateTime.Now - startTime).TotalSeconds;

            // 检查是否超过最大持续时间（仅当_maxDuration > 0时）
            if (_maxDuration > 0 && DateTime.Now >= _maxEndTime)
            {
                Logger.LogInformation("出招表执行超时，停止执行剩余指令");
                break;
            }

            ExecuteCommandLine(commandLine);
        }
        
        return (DateTime.Now - startTime).TotalSeconds;
    }

    /// <summary>
    /// 执行单行指令（支持逗号分隔的多个指令）
    /// </summary>
    /// <param name="commandLine">指令行</param>
    private void ExecuteCommandLine(string commandLine)
    {
        var commandParts = commandLine.Split(',');
        for (int i = 0; i < commandParts.Length; i++)
        {
            if (_cancellationToken.IsCancellationRequested)
                return;

            // 检查是否超过最大持续时间
            if (DateTime.Now >= _maxEndTime)
            {
                Logger.LogInformation("出招表执行超时，停止执行剩余指令");
                return;
            }

            var command = commandParts[i].Trim();
            if (string.IsNullOrEmpty(command))
                continue;

            ExecuteSingleCommand(command);
        }
    }

    /// <summary>
    /// 执行单个指令
    /// </summary>
    /// <param name="command">单个指令</param>
    private void ExecuteSingleCommand(string command)
    {
        try
        {
            // 处理带参数的指令，如 walk(s, 0.2)
            if (command.Contains('(') && command.Contains(')'))
            {
                var startIndex = command.IndexOf('(');
                var endIndex = command.IndexOf(')');
                var methodName = command.Substring(0, startIndex).Trim();
                var parameters = command.Substring(startIndex + 1, endIndex - startIndex - 1).Trim();

                ExecuteMethodWithParameters(methodName, parameters);
            }
            else
            {
                // 处理简单指令，如 e, q, attack 等
                ExecuteSimpleCommand(command);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "执行指令失败: {Command}", command);
        }
    }

    /// <summary>
    /// 执行带参数的方法
    /// </summary>
    /// <param name="methodName">方法名</param>
    /// <param name="parameters">参数字符串</param>
    private void ExecuteMethodWithParameters(string methodName, string parameters)
    {
        var args = parameters.Split(',').Select(p => p.Trim()).ToList();

        switch (methodName.ToLower())
        {
            case "walk":
                if (args.Count == 2)
                {
                    var direction = args[0];
                    var timeStr = args[1];
                    if (double.TryParse(timeStr, out var walkTimeSeconds))
                    {
                        // 检查剩余时间
                        var remainingTime = (_maxEndTime - DateTime.Now).TotalSeconds;
                        if (remainingTime <= 0)
                        {
                            Logger.LogInformation("出招表执行超时，跳过walk指令");
                            return;
                        }
                        
                        // 如果指定时间超过剩余时间，则使用剩余时间
                        var actualTimeSeconds = Math.Min(walkTimeSeconds, remainingTime);
                        _avatar.Walk(direction, (int)(actualTimeSeconds * 1000));
                    }
                }
                break;
            case "w":
            case "a":
            case "s":
            case "d":
                if (args.Count == 1 && double.TryParse(args[0], out var moveTimeSeconds))
                {
                    var remainingTime = (_maxEndTime - DateTime.Now).TotalSeconds;
                    if (remainingTime <= 0)
                    {
                        Logger.LogInformation($"出招表执行超时，跳过{methodName}指令");
                        return;
                    }
                    
                    // 如果指定时间超过剩余时间，则使用剩余时间
                    var actualTimeSeconds = Math.Min(moveTimeSeconds, remainingTime);
                    _avatar.Walk(methodName, (int)(actualTimeSeconds * 1000));
                }
                break;
            case "attack":
                if (args.Count == 1 && double.TryParse(args[0], out var attackTime))
                {
                    var remainingTime = (_maxEndTime - DateTime.Now).TotalSeconds;
                    if (remainingTime <= 0)
                    {
                        Logger.LogInformation("出招表执行超时，跳过attack指令");
                        return;
                    }
                    
                    var actualTimeSeconds = Math.Min(attackTime, remainingTime);
                    _avatar.Attack((int)(actualTimeSeconds * 1000));
                }
                else
                {
                    _avatar.Attack();
                }
                break;
            case "charge":
                if (args.Count == 1 && double.TryParse(args[0], out var chargeTime))
                {
                    var remainingTime = (_maxEndTime - DateTime.Now).TotalSeconds;
                    if (remainingTime <= 0)
                    {
                        Logger.LogInformation("出招表执行超时，跳过charge指令");
                        return;
                    }
                    
                    var actualTimeSeconds = Math.Min(chargeTime, remainingTime);
                    _avatar.Charge((int)(actualTimeSeconds * 1000));
                }
                else
                {
                    _avatar.Charge();
                }
                break;
            case "dash":
                if (args.Count == 1 && double.TryParse(args[0], out var dashTime))
                {
                    var remainingTime = (_maxEndTime - DateTime.Now).TotalSeconds;
                    if (remainingTime <= 0)
                    {
                        Logger.LogInformation("出招表执行超时，跳过dash指令");
                        return;
                    }
                    
                    var actualTimeSeconds = Math.Min(dashTime, remainingTime);
                    _avatar.Dash((int)(actualTimeSeconds * 1000));
                }
                else
                {
                    _avatar.Dash();
                }
                break;
            case "wait":
                if (args.Count == 1 && double.TryParse(args[0], out var waitTime))
                {
                    var remainingTime = (_maxEndTime - DateTime.Now).TotalSeconds;
                    if (remainingTime <= 0)
                    {
                        Logger.LogInformation("出招表执行超时，跳过wait指令");
                        return;
                    }
                    
                    var actualTimeSeconds = Math.Min(waitTime, remainingTime);
                    Sleep((int)(actualTimeSeconds * 1000), _cancellationToken);
                }
                break;
            case "mousedown":
                if (args.Count == 1)
                {
                    _avatar.MouseDown(args[0]);
                }
                else
                {
                    _avatar.MouseDown();
                }
                break;
            case "mouseup":
                if (args.Count == 1)
                {
                    _avatar.MouseUp(args[0]);
                }
                else
                {
                    _avatar.MouseUp();
                }
                break;
            case "click":
                if (args.Count == 1)
                {
                    _avatar.Click(args[0]);
                }
                else
                {
                    _avatar.Click();
                }
                break;
            case "moveby":
                if (args.Count == 2 && int.TryParse(args[0], out var x) && int.TryParse(args[1], out var y))
                {
                    _avatar.MoveBy(x, y);
                }
                break;
            case "keydown":
                if (args.Count == 1)
                {
                    _avatar.KeyDown(args[0]);
                }
                break;
            case "keyup":
                if (args.Count == 1)
                {
                    _avatar.KeyUp(args[0]);
                }
                break;
            case "keypress":
                if (args.Count == 1)
                {
                    _avatar.KeyPress(args[0]);
                }
                break;
            case "scroll":
                if (args.Count == 1 && int.TryParse(args[0], out var scrollAmount))
                {
                    _avatar.Scroll(scrollAmount);
                }
                break;
        }
    }

    /// <summary>
    /// 执行简单指令
    /// </summary>
    /// <param name="command">简单指令</param>
    private void ExecuteSimpleCommand(string command)
    {
        // 检查是否超过最大持续时间
        if (DateTime.Now >= _maxEndTime)
        {
            Logger.LogInformation("出招表执行超时，跳过简单指令");
            return;
        }

        switch (command.ToLower())
        {
            case "e":
            case "skill":
                _avatar.UseSkill();
                break;
            case "q":
            case "burst":
                _avatar.UseBurst();
                break;
            case "attack":
            case "普攻":
            case "普通攻击":
                _avatar.Attack();
                break;
            case "charge":
            case "重击":
                _avatar.Charge();
                break;
            case "jump":
            case "j":
            case "跳跃":
                _avatar.Jump();
                break;
            case "dash":
            case "冲刺":
                _avatar.Dash();
                break;
            case "ready":
            case "完成":
                _avatar.Ready();
                break;
            default:
                // 尝试作为按键处理
                try
                {
                    _avatar.KeyPress(command);
                }
                catch
                {
                    Logger.LogWarning("未知指令: {Command}", command);
                }
                break;
        }
    }
}