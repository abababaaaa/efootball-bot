/* xinput1_3.c —— EfootballBot 虚拟手柄「进程内代理」DLL
 *
 * 目的：让 bot 的手柄状态只出现在目标游戏进程里，系统 XInput 与前台其他应用完全不受影响。
 * 原理：游戏目录下的 xinput1_3.dll 先于 System32 被加载（xinput1_3 不是 KnownDLL，
 *       eFootball.exe 静态按序号导入 #2/#3）；本 DLL 转发所有调用到 System32 真身，
 *       仅当「共享内存 ACTIVE + 心跳新鲜 + 进程名在名单 + 目标位匹配」时，把 slot0
 *       替换成 Worker 写入的状态。
 *
 * 零泄漏：不创建任何设备、不注入任何进程、不碰系统 XInput 状态；其他应用调用的是
 *         System32\xinput1_3.dll 本体，看不到这里发生的一切。
 */
/* 调用方模块门禁：Steam overlay（gameoverlayrenderer64.dll）会被注入游戏进程，
 * 它内部的 XInput 轮询同样会走到本代理的导出上——若不加区分，bot 状态会"外泄"给 Steam
 * （覆盖层弹手柄提示 / Steam Input 把按键当输入）。这里用调用栈回溯找到**第一个非本模块**
 * 的调用方，只对它自己的游戏目录放行。 */
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdint.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include "pad_shared.h"

#ifndef _countof
#define _countof(a) (sizeof(a) / sizeof((a)[0]))
#endif

/* ---------------- XInput 类型/常量（不链接 xinput.lib，避免自引用） ---------------- */
#define XINPUT_GAMEPAD_DPAD_UP        0x0001
#define XINPUT_GAMEPAD_GUIDE          0x0400
/* 与 winerror.h 的 ERROR_* 同名会撞，前缀化；数值取自 xinput.h */
#define XI_ERROR_DEVICE_NOT_CONNECTED 1167L
#define XI_ERROR_EMPTY                1168L
#define XINPUT_FLAG_GAMEPAD           0x00000001
#define XINPUT_DEVTYPE_GAMEPAD        0x01
#define XINPUT_DEVSUBTYPE_GAMEPAD     0x01
#define BATTERY_TYPE_WIRED            0x01
#define BATTERY_LEVEL_FULL            0x03

typedef struct { WORD wButtons; BYTE bLeftTrigger, bRightTrigger;
                 SHORT sThumbLX, sThumbLY, sThumbRX, sThumbRY; } XINPUT_GAMEPAD;
typedef struct { DWORD dwPacketNumber; XINPUT_GAMEPAD Gamepad; } XINPUT_STATE;
typedef struct { WORD wLeftMotorSpeed, wRightMotorSpeed; } XINPUT_VIBRATION;
typedef struct { BYTE Type, SubType; WORD Flags; XINPUT_GAMEPAD Gamepad; XINPUT_VIBRATION Vibration; } XINPUT_CAPABILITIES;
typedef struct { BYTE BatteryType, BatteryLevel; } XINPUT_BATTERY_INFORMATION;
typedef struct { WORD VirtualKey; WCHAR Unicode; WORD Flags; BYTE UserIndex; BYTE HidCode; } XINPUT_KEYSTROKE;

typedef DWORD (WINAPI *PFN_GET_STATE)(DWORD, XINPUT_STATE*);
typedef DWORD (WINAPI *PFN_SET_STATE)(DWORD, XINPUT_VIBRATION*);
typedef DWORD (WINAPI *PFN_GET_CAPS)(DWORD, DWORD, XINPUT_CAPABILITIES*);
typedef void  (WINAPI *PFN_ENABLE)(BOOL);
typedef DWORD (WINAPI *PFN_GET_BATTERY)(DWORD, BYTE, XINPUT_BATTERY_INFORMATION*);
typedef DWORD (WINAPI *PFN_GET_KEYSTROKE)(DWORD, DWORD, XINPUT_KEYSTROKE*);
typedef DWORD (WINAPI *PFN_GET_GUIDS)(DWORD, GUID*, GUID*);

/* ---------------- 全局状态 ---------------- */
static struct {
    PFN_GET_STATE      GetState;
    PFN_SET_STATE      SetState;
    PFN_GET_CAPS       GetCaps;
    PFN_ENABLE         Enable;
    PFN_GET_BATTERY    GetBattery;
    PFN_GET_KEYSTROKE  GetKeystroke;
    PFN_GET_GUIDS      GetGuids;
} g_real;

static WCHAR        g_dllDir[MAX_PATH];
static WCHAR        g_iniPath[MAX_PATH];
static WCHAR        g_logPath[MAX_PATH];
static WCHAR        g_targets[8][64];
static int          g_targetCount;
static UINT         g_targetBit = PAD_TARGET_GLOBAL;
static PadSharedV1* g_shm;
static HANDLE       g_shmHandle;
static BOOL         g_whitelisted;
static BOOL         g_counterHook;
static ULONGLONG    g_heartbeatTimeout = PAD_HEARTBEAT_TIMEOUT_MS;
static volatile LONG g_initState;          /* 0=未初始化 1=初始化中 2=完成 */
static volatile LONG g_packet;
static volatile LONG g_userEnabled = 1;
static volatile LONG g_syntheticCalls;
static void*        g_steamTarget;
static HMODULE      g_selfModule;
static BOOL         g_callerScope = TRUE;
static ULONGLONG    g_lastScopeLog;
typedef USHORT (WINAPI *PFN_RTL_CAPTURE)(ULONG FramesToSkip, ULONG FramesToCapture, PVOID* BackTrace, PULONG BackTraceHash);
static PFN_RTL_CAPTURE g_capture;

/* ---------------- 日志 ---------------- */
static void LogW(const WCHAR* fmt, ...)
{
    if (!g_logPath[0]) return;
    WCHAR buf[1024];
    va_list ap; va_start(ap, fmt);
    vswprintf(buf, _countof(buf), fmt, ap);
    va_end(ap);
    buf[_countof(buf) - 1] = 0;

    /* UTF-8 落盘：不依赖 locale，MSVC / mingw 行为一致 */
    char utf8[4096];
    int n = WideCharToMultiByte(CP_UTF8, 0, buf, -1, utf8, (int)sizeof(utf8) - 1, NULL, NULL);
    if (n <= 0) return;
    utf8[n - 1] = 0;

    FILE* f = _wfopen(g_logPath, L"ab");
    if (!f) return;
    SYSTEMTIME st; GetLocalTime(&st);
    char head[64];
    int h = sprintf(head, "[%04d-%02d-%02d %02d:%02d:%02d] ",
                    st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    if (h > 0) fwrite(head, 1, (size_t)h, f);
    fwrite(utf8, 1, strlen(utf8), f);
    fwrite("\r\n", 1, 2, f);
    fclose(f);
}

/* 序号 1 占位：System32 里导出名是 DllMain（真实入口），这里放个空桩保证
 * 「按序号导入」的游戏不会因为查不到 #1 而加载失败（没有任何游戏导它）。 */
void __stdcall ProxyOrdinal1(void) { }

/* 导出函数前置声明（EnsureOwnHook 需要取地址） */
DWORD WINAPI XInputGetState(DWORD idx, XINPUT_STATE* st);

/* ---------------- 内联 JMP 改写（反 Steam overlay hook） ---------------- */
static void PatchJmp(void* at, void* to)
{
    DWORD old = 0;
    if (!VirtualProtect(at, 8, PAGE_EXECUTE_READWRITE, &old)) return;
    unsigned char* p = (unsigned char*)at;
    p[0] = 0xE9;
    int32_t rel = (int32_t)((unsigned char*)to - (p + 5));
    memcpy(p + 1, &rel, sizeof(rel));
    VirtualProtect(at, 8, old, &old);
    FlushInstructionCache(GetCurrentProcess(), at, 8);
}

static DWORD WINAPI FilterGetState(DWORD idx, XINPUT_STATE* st);

static void EnsureOwnHook(void)
{
    if (!g_counterHook) return;
    unsigned char* p = (unsigned char*)&XInputGetState;
    if (p[0] != 0xE9) return;
    int32_t rel = 0;
    memcpy(&rel, p + 1, sizeof(rel));
    void* target = (void*)(p + 5 + rel);
    if (target == (void*)&FilterGetState) return;
    g_steamTarget = target;
    PatchJmp(p, (void*)&FilterGetState);
    LogW(L"反 hook：XInputGetState 被改写为 %p，已指回本代理", target);
}

static DWORD WINAPI HookWatchdog(LPVOID)
{
    for (;;) {
        Sleep(1000);
        EnsureOwnHook();
    }
    return 0; /* 不可达，纯为消 -Wreturn-type */
}

/* ---------------- 初始化 ---------------- */
static void BuildPath(WCHAR* out, size_t cch, const WCHAR* name)
{
    size_t len = 0;
    if (cch == 0) return;
    while (g_dllDir[len] && len + 1 < cch) { out[len] = g_dllDir[len]; len++; }
    out[len] = 0;
    if (len + 1 < cch) { out[len++] = L'\\'; out[len] = 0; }
    wcsncat(out, name, cch - len - 1);
}

/* 部署脚本常把 ini 存成 UTF-8 with BOM：GetPrivateProfileStringW 会把 BOM 当成
 * 第一行的第一个字符，导致 [proxy] 段读不到（表现：白名单永远判非目标）。
 * 这里就地去掉 BOM（幂等，只改前 3 字节）。 */
static void NormalizeIni(void)
{
    HANDLE f = CreateFileW(g_iniPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return;
    BYTE head[3] = {0};
    DWORD read = 0;
    if (ReadFile(f, head, 3, &read, NULL) && read == 3 &&
        head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) {
        LARGE_INTEGER size; GetFileSizeEx(f, &size);
        DWORD rest = (DWORD)(size.QuadPart - 3);
        BYTE* buf = (BYTE*)malloc(rest ? rest : 1);
        if (buf) {
            DWORD got = 0;
            SetFilePointer(f, 3, NULL, FILE_BEGIN);
            if (ReadFile(f, buf, rest, &got, NULL) && got == rest) {
                SetFilePointer(f, 0, NULL, FILE_BEGIN);
                DWORD wrote = 0;
                if (WriteFile(f, buf, got, &wrote, NULL) && wrote == got) {
                    SetFilePointer(f, got, NULL, FILE_BEGIN);
                    SetEndOfFile(f);
                }
            }
            free(buf);
        }
    }
    CloseHandle(f);
}

static void LoadIni(void)
{
    WCHAR tmp[512];
    GetPrivateProfileStringW(L"proxy", L"TargetProcess", L"eFootball.exe", tmp, _countof(tmp), g_iniPath);
    g_targetCount = 0;
    WCHAR* cursor = tmp;
    while (*cursor && g_targetCount < 8) {
        WCHAR* start = cursor;
        while (*cursor && *cursor != L';' && *cursor != L',') cursor++;
        WCHAR* end = cursor;
        if (*cursor) *cursor++ = 0;
        while (start < end && *start == L' ') start++;
        while (end > start && end[-1] == L' ') end--;
        if (end > start) {
            size_t n = (size_t)(end - start);
            if (n > 63) n = 63;
            memcpy(g_targets[g_targetCount], start, n * sizeof(WCHAR));
            g_targets[g_targetCount][n] = 0;
            g_targetCount++;
        }
    }
    UINT id = GetPrivateProfileIntW(L"proxy", L"TargetId", 1, g_iniPath);
    g_targetBit = (id == 2) ? PAD_TARGET_CN : PAD_TARGET_GLOBAL;
    g_counterHook = GetPrivateProfileIntW(L"proxy", L"CounterHook", 1, g_iniPath) != 0;
    g_callerScope = GetPrivateProfileIntW(L"proxy", L"CallerScope", 1, g_iniPath) != 0;
    g_heartbeatTimeout = (ULONGLONG)GetPrivateProfileIntW(L"proxy", L"HeartbeatTimeoutMs",
                                                         PAD_HEARTBEAT_TIMEOUT_MS, g_iniPath);
}

static BOOL ProcessIsTarget(void)
{
    WCHAR exe[MAX_PATH] = {0};
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    const WCHAR* base = wcsrchr(exe, L'\\');
    base = base ? base + 1 : exe;
    for (int i = 0; i < g_targetCount; i++)
        if (_wcsicmp(base, g_targets[i]) == 0) return TRUE;
    return FALSE;
}

static void OpenShared(void)
{
    WCHAR name[256];
    GetPrivateProfileStringW(L"proxy", L"SharedName", PAD_SHM_NAME, name, _countof(name), g_iniPath);
    g_shmHandle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
    if (!g_shmHandle) {
        // Worker 没起 / 名字不一致：自己建一个空映射，避免崩溃，但必须留下可诊断的日志。
        g_shmHandle = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0,
                                         sizeof(PadSharedV1), name);
        LogW(L"共享内存不存在，已自建空映射：名字=%ls（Worker 未启动或 ini 里的名字不一致）", name);
    }
    if (g_shmHandle)
        g_shm = (PadSharedV1*)MapViewOfFile(g_shmHandle, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(PadSharedV1));
}

static void InitOnce(void)
{
    if (InterlockedCompareExchange(&g_initState, 1, 0) != 0) {
        while (g_initState != 2) Sleep(1);
        return;
    }

    HMODULE self = NULL;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCWSTR)&InitOnce, &self);
    g_selfModule = self;
    g_capture = (PFN_RTL_CAPTURE)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlCaptureStackBackTrace");
    GetModuleFileNameW(self, g_dllDir, MAX_PATH);
    WCHAR* slash = wcsrchr(g_dllDir, L'\\');
    if (slash) *slash = 0;
    BuildPath(g_iniPath, MAX_PATH, L"xinput_proxy.ini");
    BuildPath(g_logPath, MAX_PATH, L"xinput_proxy.log");

    NormalizeIni();
    LoadIni();
    g_whitelisted = ProcessIsTarget();

    WCHAR sys[MAX_PATH];
    GetSystemDirectoryW(sys, MAX_PATH);
    wcsncat(sys, L"\\xinput1_3.dll", MAX_PATH - wcslen(sys) - 1);
    HMODULE real = LoadLibraryExW(sys, NULL, LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!real) real = LoadLibraryExW(sys, NULL, 0);
    if (real) {
        g_real.GetState     = (PFN_GET_STATE)GetProcAddress(real, "XInputGetState");
        g_real.SetState     = (PFN_SET_STATE)GetProcAddress(real, "XInputSetState");
        g_real.GetCaps      = (PFN_GET_CAPS)GetProcAddress(real, "XInputGetCapabilities");
        g_real.Enable       = (PFN_ENABLE)GetProcAddress(real, "XInputEnable");
        g_real.GetBattery   = (PFN_GET_BATTERY)GetProcAddress(real, "XInputGetBatteryInformation");
        g_real.GetKeystroke = (PFN_GET_KEYSTROKE)GetProcAddress(real, "XInputGetKeystroke");
        g_real.GetGuids     = (PFN_GET_GUIDS)GetProcAddress(real, "XInputGetDSoundAudioDeviceGuids");
    }
    OpenShared();

    if (GetPrivateProfileIntW(L"proxy", L"Log", 1, g_iniPath))
        LogW(L"代理已加载：进程=%ls 白名单=%d 目标位=0x%X 共享内存=%p 真身=%p",
             g_whitelisted ? L"命中" : L"非目标", g_whitelisted, g_targetBit, g_shm, real);

    if (g_whitelisted && g_counterHook)
        CreateThread(NULL, 0, HookWatchdog, NULL, 0, NULL);

    InterlockedExchange(&g_initState, 2);
}

/* ---------------- 调用方模块门禁 ---------------- */

/* 返回调用方是否属于"游戏自己的目录"（游戏 exe / EF*.dll / SDL2.dll 都在同一个 Binaries\Win64）。 */
static BOOL CallerIsGameModule(void)
{
    if (!g_callerScope) return TRUE;
    if (!g_capture) return TRUE;   /* 取不到回溯 API 时退回旧行为，避免误杀输入 */

    void* frames[16];
    USHORT n = g_capture(1, 16, frames, NULL);
    for (USHORT i = 0; i < n; i++) {
        HMODULE m = NULL;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                (LPCWSTR)frames[i], &m) || !m) continue;
        if (m == g_selfModule) continue;   /* 跳过本代理自己的导出/过滤函数 */

        WCHAR path[MAX_PATH];
        if (!GetModuleFileNameW(m, path, MAX_PATH)) return FALSE;
        size_t d = wcslen(g_dllDir);
        BOOL inside = _wcsnicmp(path, g_dllDir, d) == 0 && (path[d] == L'\\' || path[d] == 0);

        ULONGLONG now = GetTickCount64();
        if (!inside && now - g_lastScopeLog > 5000) {
            g_lastScopeLog = now;
            LogW(L"非游戏模块的 XInput 调用已透传（不喂合成状态）：%ls", path);
        }
        return inside;
    }
    return FALSE;   /* 调用方不在任何模块（JIT 等）：不喂，安全优先 */
}

/* ---------------- 共享内存读取（seqlock） ---------------- */
static BOOL ReadSnapshot(PadSharedV1* out)
{
    if (!g_shm) return FALSE;
    for (int i = 0; i < 4; i++) {
        LONG64 s1 = g_shm->seq;
        if (s1 & 1) continue;
        MemoryBarrier();
        memcpy(out, g_shm, sizeof(*out));
        MemoryBarrier();
        if (g_shm->seq == s1) return TRUE;
    }
    return FALSE;
}

static BOOL BotOwns(DWORD idx, PadSharedV1* snap)
{
    if (!g_whitelisted || idx != 0) return FALSE;
    if (!CallerIsGameModule()) return FALSE;   /* Steam overlay 等非游戏模块一律透传 */
    if (InterlockedCompareExchange(&g_userEnabled, 1, 1) == 0) return FALSE; /* XInputEnable(FALSE) 期间透传 */
    if (!ReadSnapshot(snap)) return FALSE;
    if (snap->magic != PAD_SHM_MAGIC || snap->version != PAD_SHM_VERSION) return FALSE;
    if (!(snap->flags & PAD_FLAG_ACTIVE)) return FALSE;
    if (!(snap->targetMask & g_targetBit)) return FALSE;
    if (GetTickCount64() - (ULONGLONG)snap->heartbeatMs > g_heartbeatTimeout) return FALSE; /* Worker 已死 → 透传 */
    return TRUE;
}

static void NoteProxyUse(void)
{
    InterlockedIncrement(&g_syntheticCalls);
    if (g_shm) {
        g_shm->proxySeenMs = (LONG64)GetTickCount64();
        g_shm->proxyPid = GetCurrentProcessId();
        InterlockedIncrement(&g_shm->proxyCalls);
        g_shm->proxyFlags |= 1u;
    }
}

/* ---------------- 状态合成 ---------------- */
static DWORD WINAPI FilterGetState(DWORD idx, XINPUT_STATE* st)
{
    PadSharedV1 snap;
    if (st && BotOwns(idx, &snap)) {
        memset(st, 0, sizeof(*st));
        st->dwPacketNumber = (DWORD)InterlockedIncrement(&g_packet);
        if (snap.flags & PAD_FLAG_PANIC) { NoteProxyUse(); return ERROR_SUCCESS; }
        st->Gamepad.wButtons       = snap.buttons;
        st->Gamepad.bLeftTrigger   = snap.leftTrigger;
        st->Gamepad.bRightTrigger  = snap.rightTrigger;
        st->Gamepad.sThumbLX       = snap.thumbLX;
        st->Gamepad.sThumbLY       = snap.thumbLY;
        st->Gamepad.sThumbRX       = snap.thumbRX;
        st->Gamepad.sThumbRY       = snap.thumbRY;
        NoteProxyUse();
        return ERROR_SUCCESS;
    }
    if (!g_real.GetState) return XI_ERROR_DEVICE_NOT_CONNECTED;
    return g_real.GetState(idx, st);
}

/* ---------------- 导出函数（名称 + 序号，见 xinput1_3.def） ---------------- */
DWORD WINAPI XInputGetState(DWORD idx, XINPUT_STATE* st)
{
    InitOnce();
    return FilterGetState(idx, st);
}

DWORD WINAPI XInputGetStateEx(DWORD idx, XINPUT_STATE* st)
{
    InitOnce();
    return FilterGetState(idx, st);
}

DWORD WINAPI XInputSetState(DWORD idx, XINPUT_VIBRATION* vib)
{
    InitOnce();
    PadSharedV1 snap;
    if (BotOwns(idx, &snap)) { NoteProxyUse(); return ERROR_SUCCESS; } /* 震动不落到真实手柄 */
    return g_real.SetState ? g_real.SetState(idx, vib) : ERROR_DEVICE_NOT_CONNECTED;
}

DWORD WINAPI XInputGetCapabilities(DWORD idx, DWORD flags, XINPUT_CAPABILITIES* caps)
{
    InitOnce();
    PadSharedV1 snap;
    if (caps && BotOwns(idx, &snap)) {
        memset(caps, 0, sizeof(*caps));
        caps->Type      = XINPUT_DEVTYPE_GAMEPAD;
        caps->SubType   = XINPUT_DEVSUBTYPE_GAMEPAD;
        caps->Flags     = 0;
        caps->Gamepad.wButtons       = 0xF3FF;
        caps->Gamepad.bLeftTrigger   = 0xFF;
        caps->Gamepad.bRightTrigger  = 0xFF;
        caps->Gamepad.sThumbLX       = (SHORT)0xFFC0;
        caps->Gamepad.sThumbLY       = (SHORT)0xFFC0;
        caps->Gamepad.sThumbRX       = (SHORT)0xFFC0;
        caps->Gamepad.sThumbRY       = (SHORT)0xFFC0;
        caps->Vibration.wLeftMotorSpeed  = 0xFF;
        caps->Vibration.wRightMotorSpeed = 0xFF;
        NoteProxyUse();
        return ERROR_SUCCESS;
    }
    return g_real.GetCaps ? g_real.GetCaps(idx, flags, caps) : ERROR_DEVICE_NOT_CONNECTED;
}

void WINAPI XInputEnable(BOOL enable)
{
    InitOnce();
    InterlockedExchange(&g_userEnabled, enable ? 1 : 0);
    if (g_real.Enable) g_real.Enable(enable);
}

DWORD WINAPI XInputGetBatteryInformation(DWORD idx, BYTE devType, XINPUT_BATTERY_INFORMATION* b)
{
    InitOnce();
    PadSharedV1 snap;
    if (b && BotOwns(idx, &snap)) {
        b->BatteryType  = BATTERY_TYPE_WIRED;
        b->BatteryLevel = BATTERY_LEVEL_FULL;
        NoteProxyUse();
        return ERROR_SUCCESS;
    }
    return g_real.GetBattery ? g_real.GetBattery(idx, devType, b) : ERROR_DEVICE_NOT_CONNECTED;
}

DWORD WINAPI XInputGetKeystroke(DWORD idx, DWORD reserved, XINPUT_KEYSTROKE* ks)
{
    InitOnce();
    PadSharedV1 snap;
    if (BotOwns(idx, &snap)) { NoteProxyUse(); return XI_ERROR_EMPTY; }
    return g_real.GetKeystroke ? g_real.GetKeystroke(idx, reserved, ks) : ERROR_DEVICE_NOT_CONNECTED;
}

DWORD WINAPI XInputGetDSoundAudioDeviceGuids(DWORD idx, GUID* render, GUID* capture)
{
    InitOnce();
    PadSharedV1 snap;
    if (BotOwns(idx, &snap)) {
        if (render)  memset(render, 0, sizeof(GUID));
        if (capture) memset(capture, 0, sizeof(GUID));
        return ERROR_SUCCESS;
    }
    return g_real.GetGuids ? g_real.GetGuids(idx, render, capture) : ERROR_DEVICE_NOT_CONNECTED;
}

/* 以下为未文档化接口的占位：返回“未连接”，避免依赖真身缺失 */
/* xinput1_4 独有导出：音频设备 ID（1_3 没有；靠 .def 决定导不导出） */
DWORD WINAPI XInputGetAudioDeviceIds(DWORD idx, WCHAR* render, UINT* renderCount,
                                     WCHAR* capture, UINT* captureCount)
{
    InitOnce();
    PadSharedV1 snap;
    if (BotOwns(idx, &snap)) {
        if (render && renderCount) *renderCount = 0;
        if (capture && captureCount) *captureCount = 0;
        NoteProxyUse();
        return ERROR_SUCCESS;
    }
    return XI_ERROR_DEVICE_NOT_CONNECTED;
}

DWORD WINAPI XInputWaitForGuideButton(DWORD idx, DWORD flags, void* p) { (void)idx; (void)flags; (void)p; return XI_ERROR_DEVICE_NOT_CONNECTED; }
DWORD WINAPI XInputCancelGuideButtonWait(DWORD idx) { (void)idx; return XI_ERROR_DEVICE_NOT_CONNECTED; }
DWORD WINAPI XInputPowerOffController(DWORD idx) { (void)idx; return XI_ERROR_DEVICE_NOT_CONNECTED; }

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)inst; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(inst);
    return TRUE;
}