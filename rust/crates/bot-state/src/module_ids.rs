//! 全部模块 id 的唯一定义处（= 管道协议 start 命令的 mode 字符串）。
//! 对应 C# `Core/Bot/Modules/ModuleIds.cs`，字面量只在本文件出现。

/// 引擎的「非模块模式」：不进注册表，也不接管手柄。
pub struct EngineModes;

impl EngineModes {
    /// 纯感知观察（--console / 排障用）
    pub const OBSERVE: &'static str = "observe";
}

/// 挂机模块 id（唯一来源）。
pub struct ModuleIds;

impl ModuleIds {
    // ---- 国际服 ----
    pub const INTL_MY_LEAGUE: &'static str = "intl";
    pub const INTL_WEEKLY: &'static str = "intlweekly";
    pub const INTL_AI_LADDER: &'static str = "intlladder";
    pub const INTL_STUD_BUY: &'static str = "intlstudbuy";
    pub const INTL_STUD_TRAIN: &'static str = "intlstudtrain";
    pub const INTL_SWAP_TEST: &'static str = "intlswaptest";
    pub const INTL_TRAIN_FRAMES: &'static str = "intltrainframes";
    pub const INTL_PRE_MATCH: &'static str = "intlprematch";

    // ---- 国服 ----
    pub const CN_LADDER: &'static str = "cndaily";
    pub const CN_WEEKLY: &'static str = "cnweekly";
    pub const CN_MY_LEAGUE: &'static str = "cnmyleague";
    pub const CN_PRE_MATCH: &'static str = "cnprematch";
}

/// 全量挂机模块清单（顺序与 C# BotModuleCatalog 一致：国际服在前，国服在后）。
/// UI 的接力下拉候选、跨服校验都应由此派生，不要另写一份。
pub const ALL_MODULES: &[&str] = &[
    ModuleIds::INTL_MY_LEAGUE,
    ModuleIds::INTL_WEEKLY,
    ModuleIds::INTL_AI_LADDER,
    ModuleIds::INTL_STUD_BUY,
    ModuleIds::INTL_STUD_TRAIN,
    ModuleIds::INTL_SWAP_TEST,
    ModuleIds::INTL_TRAIN_FRAMES,
    ModuleIds::INTL_PRE_MATCH,
    ModuleIds::CN_LADDER,
    ModuleIds::CN_WEEKLY,
    ModuleIds::CN_MY_LEAGUE,
    ModuleIds::CN_PRE_MATCH,
];

/// 把一个模块 id 转成 start 命令消息（GUI 选中模块后下发给 Worker）。
/// 这是状态层唯一需要协议层的地方，也是保留 bot-ipc 依赖的正当理由。
pub fn start_command(module_id: &str) -> bot_ipc::Msg {
    bot_ipc::Msg::cmd_start(module_id)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn module_id_becomes_start_command() {
        let msg = start_command(ModuleIds::CN_WEEKLY);
        let line = msg.to_line();
        assert!(line.contains(r#""cmd":"start""#));
        assert!(line.contains(r#""mode":"cnweekly""#));
    }

    #[test]
    fn all_modules_has_no_duplicates() {
        let mut seen = std::collections::HashSet::new();
        for id in ALL_MODULES {
            assert!(seen.insert(*id), "duplicate module id: {id}");
        }
    }

    #[test]
    fn count_matches_csharp_catalog() {
        // 8 国际服 + 4 国服 = 12（与 ModuleIds.cs 一致）
        assert_eq!(ALL_MODULES.len(), 12);
    }
}
