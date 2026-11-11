/* pad_shared.h —— EfootballBot 虚拟手柄共享内存 ABI v1
 * 代理 DLL（xinput1_3.dll）与 Worker 之间唯一的耦合点。
 * 规则：只允许追加字段；已有字段的偏移/语义永不改动（版本号 + structSize 双校验）。
 */
#pragma once
#include <stdint.h>
#include <windows.h>

#define PAD_SHM_NAME      L"Local\\EfootballBot.Pad.v1"
#define PAD_SHM_MAGIC     0x44415045u   /* 'EPAD' */
#define PAD_SHM_VERSION   1u

/* flags */
#define PAD_FLAG_ACTIVE   0x0001u  /* bot 接管 slot0；0 = 代理完全透传 */
#define PAD_FLAG_READY    0x0002u  /* Worker 已完成初始化（供 UI/诊断） */
#define PAD_FLAG_PANIC    0x0004u  /* 紧急全松手（停止挂机/看门狗） */

/* targetMask 位：一个进程一个 bit，允许同一 Worker 喂多个目标 */
#define PAD_TARGET_GLOBAL 0x0001u  /* 国际服 eFootball.exe */
#define PAD_TARGET_CN     0x0002u  /* 国服 eFootballOnline.exe */
#define PAD_TARGET_ALL    0xFFFFFFFFu

/* 心跳超时（ms）：超过即视为 Worker 已死，代理立刻退回透传（fail-safe） */
#define PAD_HEARTBEAT_TIMEOUT_MS 1500

#pragma pack(push, 1)
typedef struct PadSharedV1 {
    uint32_t magic;          /* PAD_SHM_MAGIC */
    uint32_t version;        /* PAD_SHM_VERSION */
    uint32_t structSize;     /* sizeof(PadSharedV1) */

    volatile LONG64 seq;         /* seqlock：奇数=正在写，偶数=稳定 */
    volatile LONG64 heartbeatMs; /* Worker 的 GetTickCount64() */
    uint32_t writerPid;
    uint32_t flags;              /* PAD_FLAG_* */
    uint32_t targetMask;         /* PAD_TARGET_* */

    /* 以下 16 字节为一次原子快照（seqlock 保护） */
    uint16_t buttons;        /* XINPUT_GAMEPAD_* 位掩码 */
    uint8_t  leftTrigger;    /* 0..255 */
    uint8_t  rightTrigger;
    int16_t  thumbLX;
    int16_t  thumbLY;
    int16_t  thumbRX;
    int16_t  thumbRY;

    /* 代理回写（Worker 据此判断“游戏真的在读我们的状态”） */
    volatile LONG64 proxySeenMs;
    uint32_t proxyPid;
    volatile LONG proxyCalls;
    uint32_t proxyFlags;     /* bit0=本进程已加载并接管 */

    uint32_t reserved[8];
} PadSharedV1;
#pragma pack(pop)

#ifdef __cplusplus
static_assert(sizeof(PadSharedV1) == 104, "PadSharedV1 布局变化必须升版本号");
#endif
