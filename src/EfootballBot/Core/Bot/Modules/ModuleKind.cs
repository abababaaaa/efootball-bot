// ===========================================================================
// 架构锁 · 注册表层 Core/Bot/Modules（v7.2）
// 本层职责：模块清单 —— id / 服务器分区 / 场景或工具 / 工厂 / 是否可接力。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 模块 id 字面量只许出现在本层；别处一律用 ModuleIds 常量
//   · 具体场景只许在本层 new（BotModuleCatalog 是唯一工厂入口）
//   · 本层不写业务逻辑，不引用任何场景的内部状态
// 改写法前先读 docs/MODULES.md §7 与 AGENTS.md；除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Bot.Modules;

/// <summary>
/// 模块类型：决定一个模块在 GUI 里是不是「开始挂机」按钮，以及引擎是否接管手柄。
/// </summary>
public enum ModuleKind
{
    /// <summary>挂机场景（引擎建会话 + 接虚拟手柄 + 场景决策循环）。</summary>
    Scenario,
    /// <summary>本地工具页（例如赛前快捷设置：开游戏设置工具/启动游戏），不接引擎、不占手柄。</summary>
    Tool,
}





