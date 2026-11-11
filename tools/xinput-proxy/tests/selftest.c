/* selftest.c —— 代理 DLL 的功能自测（无需游戏）
 *
 * 思路：本进程伪造 Worker 侧共享内存，然后 LoadLibrary 代理 DLL，按序号取函数指针，
 *       验证「ACTIVE + 心跳新鲜 + 目标位命中」时 slot0 返回合成状态，其余情况一律透传。
 * 这能证明：ABI 偏移一致、序号导出可用、透传链路（System32）正常、心跳失效 fail-safe 生效。
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stddef.h>
#include <stdbool.h>
#include <stdio.h>
#include <string.h>
#include "../pad_shared.h"

#define XI_ERROR_DEVICE_NOT_CONNECTED 1167L
#define XI_ERROR_EMPTY                1168L

typedef struct { WORD wButtons; BYTE bLeftTrigger, bRightTrigger;
                 SHORT sThumbLX, sThumbLY, sThumbRX, sThumbRY; } XI_GAMEPAD;
typedef struct { DWORD dwPacketNumber; XI_GAMEPAD Gamepad; } XI_STATE;
typedef struct { WORD wLeftMotorSpeed, wRightMotorSpeed; } XI_VIBRATION;
typedef struct { BYTE Type, SubType; WORD Flags; XI_GAMEPAD Gamepad; XI_VIBRATION Vibration; } XI_CAPS;
typedef struct { WORD VirtualKey; WCHAR Unicode; WORD Flags; BYTE UserIndex; BYTE HidCode; } XI_KEYSTROKE;

static PadSharedV1* g_shm;
static int g_fail;
static int g_pass;

static void Check(int cond, const char* what, long long got, long long want)
{
    if (cond) { g_pass++; printf("  PASS  %s\n", what); }
    else { g_fail++; printf("  FAIL  %s (got=%lld want=%lld)\n", what, got, want); }
}

static void Publish(WORD buttons, short lx, short ly)
{
    g_shm->seq++;
    MemoryBarrier();
    g_shm->buttons = buttons;
    g_shm->leftTrigger = 11;
    g_shm->rightTrigger = 22;
    g_shm->thumbLX = lx; g_shm->thumbLY = ly;
    g_shm->thumbRX = 0; g_shm->thumbRY = 0;
    MemoryBarrier();
    g_shm->seq++;
    g_shm->heartbeatMs = (LONG64)GetTickCount64();
}

int main(int argc, char** argv)
{
    /* ABI 自描述：与 C# SharedPadWriter 的常量逐个比对（跨语言唯一真凭据是偏移，不是名字） */
    printf("ABI size=%zu seq=%zu hb=%zu flags=%zu mask=%zu buttons=%zu lt=%zu rt=%zu lx=%zu ly=%zu rx=%zu ry=%zu seen=%zu calls=%zu\n",
           sizeof(PadSharedV1),
           offsetof(PadSharedV1, seq), offsetof(PadSharedV1, heartbeatMs),
           offsetof(PadSharedV1, flags), offsetof(PadSharedV1, targetMask),
           offsetof(PadSharedV1, buttons), offsetof(PadSharedV1, leftTrigger),
           offsetof(PadSharedV1, rightTrigger), offsetof(PadSharedV1, thumbLX),
           offsetof(PadSharedV1, thumbLY), offsetof(PadSharedV1, thumbRX),
           offsetof(PadSharedV1, thumbRY), offsetof(PadSharedV1, proxySeenMs),
           offsetof(PadSharedV1, proxyCalls));

    const char* dllOverride = NULL;
    bool expectPassthrough = false;
    bool expectForeignCaller = false;
    for (int i = 1; i < argc; i++) {
        if (strncmp(argv[i], "--dll=", 6) == 0) dllOverride = argv[i] + 6;
        else if (strcmp(argv[i], "--expect-passthrough") == 0) expectPassthrough = true;
        else if (strcmp(argv[i], "--expect-foreign-caller") == 0) expectForeignCaller = true;
    }
    const char* shmName = "Local\\EfootballBot.Pad.v1";
    HANDLE map = CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0,
                                    sizeof(PadSharedV1), shmName);
    if (!map) { printf("FATAL: CreateFileMapping failed %lu\n", GetLastError()); return 2; }
    g_shm = (PadSharedV1*)MapViewOfFile(map, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(PadSharedV1));
    if (!g_shm) { printf("FATAL: MapViewOfFile failed\n"); return 2; }
    memset(g_shm, 0, sizeof(*g_shm));
    g_shm->magic = PAD_SHM_MAGIC;
    g_shm->version = PAD_SHM_VERSION;
    g_shm->structSize = (uint32_t)sizeof(PadSharedV1);
    g_shm->writerPid = GetCurrentProcessId();
    g_shm->targetMask = PAD_TARGET_GLOBAL;
    g_shm->flags = PAD_FLAG_ACTIVE | PAD_FLAG_READY;

    /* 代理按“自己所在目录”找 ini，因此 DLL 与 ini 都在 exe 同目录 */
    char dllPath[MAX_PATH];
    if (dllOverride) {
        strncpy_s(dllPath, sizeof(dllPath), dllOverride, _TRUNCATE);
    } else {
        GetModuleFileNameA(NULL, dllPath, MAX_PATH);
        char* slash = strrchr(dllPath, '\\');
        if (slash) strcpy(slash + 1, "xinput1_3.dll");
    }
    printf("== load %s ==\n", dllPath);
    HMODULE h = LoadLibraryA(dllPath);
    if (!h) { printf("FATAL: LoadLibrary failed %lu\n", GetLastError()); return 2; }

    /* 按序号取（与 eFootball 的导入方式一致） */
    typedef DWORD (WINAPI *PFN_GET)(DWORD, XI_STATE*);
    typedef DWORD (WINAPI *PFN_SET)(DWORD, XI_VIBRATION*);
    typedef DWORD (WINAPI *PFN_CAPS)(DWORD, DWORD, XI_CAPS*);
    typedef DWORD (WINAPI *PFN_KEY)(DWORD, DWORD, XI_KEYSTROKE*);
    PFN_GET  GetState  = (PFN_GET)GetProcAddress(h, MAKEINTRESOURCEA(2));
    PFN_SET  SetState  = (PFN_SET)GetProcAddress(h, MAKEINTRESOURCEA(3));
    PFN_CAPS GetCaps   = (PFN_CAPS)GetProcAddress(h, MAKEINTRESOURCEA(4));
    PFN_KEY  GetKey    = (PFN_KEY)GetProcAddress(h, MAKEINTRESOURCEA(8));
    Check(GetState && SetState && GetCaps && GetKey, "按序号取到 #2/#3/#4/#8", 0, 0);

    XI_STATE st; memset(&st, 0, sizeof(st));
    Publish(0x1000 /* XINPUT_GAMEPAD_A */, 12345, -12345);

    if (expectForeignCaller) {
        DWORD r0 = GetState(0, &st);
        bool leaked = (r0 == 0 && st.Gamepad.sThumbLX == 12345 && st.Gamepad.wButtons == 0x1000);
        Check(!leaked, "非游戏目录的调用方（模拟 Steam overlay）：拿不到 bot 状态", leaked ? 1 : 0, 0);
        printf("\n%d passed, %d failed\n", g_pass, g_fail);
        FreeLibrary(h);
        return g_fail == 0 ? 0 : 1;
    }

    if (expectPassthrough) {
        DWORD r0 = GetState(0, &st);
        bool leaked = (r0 == 0 && st.Gamepad.sThumbLX == 12345 && st.Gamepad.wButtons == 0x1000);
        Check(!leaked, "非白名单进程：拿不到 bot 状态（不串台）", leaked ? 1 : 0, 0);
        printf("\n%d passed, %d failed\n", g_pass, g_fail);
        FreeLibrary(h);
        return g_fail == 0 ? 0 : 1;
    }

    DWORD r = GetState(0, &st);
    Check(r == 0 && st.Gamepad.wButtons == 0x1000 && st.Gamepad.sThumbLX == 12345
          && st.Gamepad.bLeftTrigger == 11, "ACTIVE: slot0 = 共享内存快照", r, 0);

    st.dwPacketNumber = 0; memset(&st, 0, sizeof(st));
    r = GetState(1, &st);
    Check(r == XI_ERROR_DEVICE_NOT_CONNECTED, "slot1 透传（无设备 → 1167）", r, XI_ERROR_DEVICE_NOT_CONNECTED);

    XI_CAPS caps; memset(&caps, 0, sizeof(caps));
    r = GetCaps(0, 0, &caps);
    Check(r == 0 && caps.Type == 1 && caps.SubType == 1 && caps.Gamepad.wButtons != 0,
          "ACTIVE: 能力表 = 标准 Xbox360 手柄", r, 0);

    r = SetState(0, NULL);
    Check(r == 0, "ACTIVE: SetState 返回成功（不震真实手柄）", r, 0);

    XI_KEYSTROKE ks; memset(&ks, 0, sizeof(ks));
    r = GetKey(0, 0, &ks);
    Check(r == XI_ERROR_EMPTY, "ACTIVE: GetKeystroke = 1168", r, XI_ERROR_EMPTY);

    /* 心跳失效 → 立刻透传 */
    g_shm->heartbeatMs = 0;
    memset(&st, 0, sizeof(st));
    r = GetState(0, &st);
    Check(!(r == 0 && st.Gamepad.sThumbLX == 12345), "心跳过期: 不再返回合成状态", r, 0);

    /* 目标位不匹配 → 透传 */
    Publish(0x1000, 12345, -12345);
    g_shm->targetMask = PAD_TARGET_CN;
    memset(&st, 0, sizeof(st));
    r = GetState(0, &st);
    Check(!(r == 0 && st.Gamepad.sThumbLX == 12345), "目标位不匹配: 透传", r, 0);
    g_shm->targetMask = PAD_TARGET_GLOBAL;

    /* ACTIVE=0 → 透传 */
    Publish(0x1000, 12345, -12345);
    g_shm->flags = 0;
    memset(&st, 0, sizeof(st));
    r = GetState(0, &st);
    Check(!(r == 0 && st.Gamepad.sThumbLX == 12345), "ACTIVE=0: 透传（游戏回到真实手柄）", r, 0);

    /* PANIC → 全松手，避免卡键 */
    g_shm->flags = PAD_FLAG_ACTIVE | PAD_FLAG_PANIC;
    Publish(0x1000, 12345, -12345);
    memset(&st, 0, sizeof(st));
    r = GetState(0, &st);
    Check(r == 0 && st.Gamepad.wButtons == 0 && st.Gamepad.sThumbLX == 0,
          "PANIC: 全零状态（无卡键）", r, 0);

    Check(g_shm->proxyCalls > 0, "代理已回写 proxyCalls", g_shm->proxyCalls, 1);
    Check(g_shm->proxyPid == GetCurrentProcessId(), "代理回写 proxyPid = 本进程", g_shm->proxyPid, GetCurrentProcessId());
    Check(g_shm->proxySeenMs > 0, "代理回写 proxySeenMs", g_shm->proxySeenMs, 1);

    printf("\n%d passed, %d failed\n", g_pass, g_fail);
    FreeLibrary(h);
    return g_fail == 0 ? 0 : 1;
}