// winmm.dll proxy for Call of Duty: Black Ops III
// Hides most audio devices FROM THE GAME ONLY so its startup device scan
// doesn't choke on a large number of enabled endpoints (the classic BO3
// "sound plays, screen stays black" hang). Everything not related to device
// enumeration is forwarded to the real winmm (shipped alongside as
// winmm_real.dll), so system-wide audio is completely unaffected.
//
// Config: bo3_audio.cfg in the same folder (optional). Keys (one per line):
//   keep=<substring>   only devices whose name contains a keep substring are
//                      shown to the game (case-insensitive). Repeatable.
//   block=<substring>  hide devices matching this even if they match a keep.
//   showall=1          disable all filtering (devices pass through unchanged).
//   log=1              append a report to bo3_audio.log each time the game
//                      scans devices (useful for verifying / tuning).
// If no keep= lines are present, a built-in default keep list is used.

#define WIN32_LEAN_AND_MEAN
#define _WINMM_            // make WINMMAPI expand to nothing (we ARE winmm)
#include <windows.h>
#include <mmsystem.h>
#include <string>
#include <vector>
#include <fstream>
#include <sstream>
#include "exports_gen.h"   // /EXPORT pragmas: forwarders + our overrides

// ---- real winmm entry points we call through ----
typedef UINT     (WINAPI *t_woNum)(void);
typedef MMRESULT (WINAPI *t_woCapsA)(UINT_PTR, LPWAVEOUTCAPSA, UINT);
typedef MMRESULT (WINAPI *t_woCapsW)(UINT_PTR, LPWAVEOUTCAPSW, UINT);
typedef MMRESULT (WINAPI *t_woOpen)(LPHWAVEOUT, UINT, LPCWAVEFORMATEX, DWORD_PTR, DWORD_PTR, DWORD);
typedef UINT     (WINAPI *t_wiNum)(void);
typedef MMRESULT (WINAPI *t_wiCapsA)(UINT_PTR, LPWAVEINCAPSA, UINT);
typedef MMRESULT (WINAPI *t_wiCapsW)(UINT_PTR, LPWAVEINCAPSW, UINT);
typedef MMRESULT (WINAPI *t_wiOpen)(LPHWAVEIN, UINT, LPCWAVEFORMATEX, DWORD_PTR, DWORD_PTR, DWORD);
typedef UINT     (WINAPI *t_mxNum)(void);
typedef MMRESULT (WINAPI *t_mxCapsA)(UINT_PTR, LPMIXERCAPSA, UINT);
typedef MMRESULT (WINAPI *t_mxCapsW)(UINT_PTR, LPMIXERCAPSW, UINT);
typedef MMRESULT (WINAPI *t_mxOpen)(LPHMIXER, UINT, DWORD_PTR, DWORD_PTR, DWORD);

static HMODULE   g_real = NULL;
static t_woNum   r_woNum;   static t_woCapsA r_woCapsA; static t_woCapsW r_woCapsW; static t_woOpen r_woOpen;
static t_wiNum   r_wiNum;   static t_wiCapsA r_wiCapsA; static t_wiCapsW r_wiCapsW; static t_wiOpen r_wiOpen;
static t_mxNum   r_mxNum;   static t_mxCapsA r_mxCapsA; static t_mxCapsW r_mxCapsW; static t_mxOpen r_mxOpen;

static std::wstring g_dir, g_logPath;
static std::vector<std::wstring> g_keep, g_block;
static bool g_showall = false, g_log = false;
static INIT_ONCE g_once = INIT_ONCE_STATIC_INIT;

static std::wstring lower(const std::wstring &s) {
    std::wstring o = s;
    for (size_t i = 0; i < o.size(); i++) o[i] = (wchar_t)towlower(o[i]);
    return o;
}
static std::wstring trim(const std::wstring &s) {
    size_t a = s.find_first_not_of(L" \t\r\n");
    if (a == std::wstring::npos) return L"";
    size_t b = s.find_last_not_of(L" \t\r\n");
    return s.substr(a, b - a + 1);
}

static void ownDir(std::wstring &dir) {
    HMODULE h = NULL;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCWSTR)&ownDir, &h);
    wchar_t buf[MAX_PATH]; buf[0] = 0;
    GetModuleFileNameW(h, buf, MAX_PATH);
    std::wstring p(buf);
    size_t s = p.find_last_of(L"\\/");
    dir = (s == std::wstring::npos) ? L"" : p.substr(0, s + 1);
}

static void loadConfig() {
    std::wifstream f((g_dir + L"bo3_audio.cfg").c_str());
    if (!f.is_open()) return;
    std::wstring line;
    while (std::getline(f, line)) {
        line = trim(line);
        if (line.empty() || line[0] == L'#' || line[0] == L';') continue;
        size_t eq = line.find(L'=');
        if (eq == std::wstring::npos) continue;
        std::wstring k = lower(trim(line.substr(0, eq)));
        std::wstring v = trim(line.substr(eq + 1));
        if      (k == L"keep"  && !v.empty()) g_keep.push_back(lower(v));
        else if (k == L"block" && !v.empty()) g_block.push_back(lower(v));
        else if (k == L"showall") g_showall = (v == L"1" || lower(v) == L"true");
        else if (k == L"log")     g_log     = (v == L"1" || lower(v) == L"true");
    }
}

// built-in defaults (lowercase, matched as substrings of the possibly
// 31-char-truncated device name). Tuned for this PC's real hardware.
static bool defaultKeep(const std::wstring &n) {
    static const wchar_t *k[] = {
        L"usb2.0 device",      // headset in/out
        L"esi audio device",   // Amber i1 interface (main speaker + 1&2)
        L"amber i1 1&2",       // Amber capture
        L"voicemeeter input",  // default Windows output (keep it selectable)
        L"realtek",            // onboard, in case it's ever the default
        L"nvidia high",        // HDMI/DP monitor audio (first only via keep)
    };
    for (size_t i = 0; i < sizeof(k) / sizeof(k[0]); i++)
        if (n.find(k[i]) != std::wstring::npos) return true;
    return false;
}

static BOOL CALLBACK initOnce(PINIT_ONCE, PVOID, PVOID *) {
    ownDir(g_dir);
    g_logPath = g_dir + L"bo3_audio.log";
    loadConfig();
    g_real = LoadLibraryW((g_dir + L"winmm_real.dll").c_str());
    if (!g_real) g_real = GetModuleHandleW(L"winmm_real.dll");
    if (g_real) {
        r_woNum   = (t_woNum)  GetProcAddress(g_real, "waveOutGetNumDevs");
        r_woCapsA = (t_woCapsA)GetProcAddress(g_real, "waveOutGetDevCapsA");
        r_woCapsW = (t_woCapsW)GetProcAddress(g_real, "waveOutGetDevCapsW");
        r_woOpen  = (t_woOpen) GetProcAddress(g_real, "waveOutOpen");
        r_wiNum   = (t_wiNum)  GetProcAddress(g_real, "waveInGetNumDevs");
        r_wiCapsA = (t_wiCapsA)GetProcAddress(g_real, "waveInGetDevCapsA");
        r_wiCapsW = (t_wiCapsW)GetProcAddress(g_real, "waveInGetDevCapsW");
        r_wiOpen  = (t_wiOpen) GetProcAddress(g_real, "waveInOpen");
        r_mxNum   = (t_mxNum)  GetProcAddress(g_real, "mixerGetNumDevs");
        r_mxCapsA = (t_mxCapsA)GetProcAddress(g_real, "mixerGetDevCapsA");
        r_mxCapsW = (t_mxCapsW)GetProcAddress(g_real, "mixerGetDevCapsW");
        r_mxOpen  = (t_mxOpen) GetProcAddress(g_real, "mixerOpen");
    }
    return TRUE;
}
static void ensureInit() { InitOnceExecuteOnce(&g_once, initOnce, NULL, NULL); }

static bool keepName(const wchar_t *nameW) {
    if (g_showall) return true;
    std::wstring n = lower(nameW);
    for (size_t i = 0; i < g_block.size(); i++)
        if (!g_block[i].empty() && n.find(g_block[i]) != std::wstring::npos) return false;
    if (g_keep.empty()) return defaultKeep(n);
    for (size_t i = 0; i < g_keep.size(); i++)
        if (!g_keep[i].empty() && n.find(g_keep[i]) != std::wstring::npos) return true;
    return false;
}

static void logLine(const std::wstring &s) {
    if (!g_log) return;
    std::wofstream f(g_logPath.c_str(), std::ios::app);
    if (f.is_open()) f << s << L"\r\n";
}

// ---- map builders: game-visible index -> real device index ----
static std::vector<UINT> mapWaveOut() {
    std::vector<UINT> m;
    UINT n = r_woNum ? r_woNum() : 0;
    logLine(L"[waveOut] real devices = " + std::to_wstring(n));
    for (UINT i = 0; i < n; i++) {
        WAVEOUTCAPSW c; ZeroMemory(&c, sizeof(c));
        bool keep = true;
        if (r_woCapsW && r_woCapsW(i, &c, sizeof(c)) == MMSYSERR_NOERROR) keep = keepName(c.szPname);
        if (keep) m.push_back(i);
        logLine((keep ? L"  show  " : L"  hide  ") + std::wstring(c.szPname));
    }
    logLine(L"[waveOut] shown to game = " + std::to_wstring(m.size()));
    return m;
}
static std::vector<UINT> mapWaveIn() {
    std::vector<UINT> m;
    UINT n = r_wiNum ? r_wiNum() : 0;
    logLine(L"[waveIn] real devices = " + std::to_wstring(n));
    for (UINT i = 0; i < n; i++) {
        WAVEINCAPSW c; ZeroMemory(&c, sizeof(c));
        bool keep = true;
        if (r_wiCapsW && r_wiCapsW(i, &c, sizeof(c)) == MMSYSERR_NOERROR) keep = keepName(c.szPname);
        if (keep) m.push_back(i);
        logLine((keep ? L"  show  " : L"  hide  ") + std::wstring(c.szPname));
    }
    logLine(L"[waveIn] shown to game = " + std::to_wstring(m.size()));
    return m;
}
static std::vector<UINT> mapMixer() {
    std::vector<UINT> m;
    UINT n = r_mxNum ? r_mxNum() : 0;
    logLine(L"[mixer] real devices = " + std::to_wstring(n));
    for (UINT i = 0; i < n; i++) {
        MIXERCAPSW c; ZeroMemory(&c, sizeof(c));
        bool keep = true;
        if (r_mxCapsW && r_mxCapsW(i, &c, sizeof(c)) == MMSYSERR_NOERROR) keep = keepName(c.szPname);
        if (keep) m.push_back(i);
        logLine((keep ? L"  show  " : L"  hide  ") + std::wstring(c.szPname));
    }
    logLine(L"[mixer] shown to game = " + std::to_wstring(m.size()));
    return m;
}

static bool isMapper(UINT id) { return id == (UINT)WAVE_MAPPER; } // 0xFFFFFFFF

// ============================ exported overrides ============================
extern "C" {

UINT WINAPI waveOutGetNumDevs(void) {
    ensureInit();
    return (UINT)mapWaveOut().size();
}
MMRESULT WINAPI waveOutGetDevCapsA(UINT_PTR id, LPWAVEOUTCAPSA p, UINT cb) {
    ensureInit();
    if (isMapper((UINT)id)) return r_woCapsA(id, p, cb);
    std::vector<UINT> m = mapWaveOut();
    if (id >= m.size()) return MMSYSERR_BADDEVICEID;
    return r_woCapsA(m[id], p, cb);
}
MMRESULT WINAPI waveOutGetDevCapsW(UINT_PTR id, LPWAVEOUTCAPSW p, UINT cb) {
    ensureInit();
    if (isMapper((UINT)id)) return r_woCapsW(id, p, cb);
    std::vector<UINT> m = mapWaveOut();
    if (id >= m.size()) return MMSYSERR_BADDEVICEID;
    return r_woCapsW(m[id], p, cb);
}
MMRESULT WINAPI waveOutOpen(LPHWAVEOUT ph, UINT id, LPCWAVEFORMATEX f, DWORD_PTR cb, DWORD_PTR in, DWORD fdw) {
    ensureInit();
    if (!isMapper(id)) {
        std::vector<UINT> m = mapWaveOut();
        if (id < m.size()) id = m[id];
    }
    return r_woOpen(ph, id, f, cb, in, fdw);
}

UINT WINAPI waveInGetNumDevs(void) {
    ensureInit();
    return (UINT)mapWaveIn().size();
}
MMRESULT WINAPI waveInGetDevCapsA(UINT_PTR id, LPWAVEINCAPSA p, UINT cb) {
    ensureInit();
    if (isMapper((UINT)id)) return r_wiCapsA(id, p, cb);
    std::vector<UINT> m = mapWaveIn();
    if (id >= m.size()) return MMSYSERR_BADDEVICEID;
    return r_wiCapsA(m[id], p, cb);
}
MMRESULT WINAPI waveInGetDevCapsW(UINT_PTR id, LPWAVEINCAPSW p, UINT cb) {
    ensureInit();
    if (isMapper((UINT)id)) return r_wiCapsW(id, p, cb);
    std::vector<UINT> m = mapWaveIn();
    if (id >= m.size()) return MMSYSERR_BADDEVICEID;
    return r_wiCapsW(m[id], p, cb);
}
MMRESULT WINAPI waveInOpen(LPHWAVEIN ph, UINT id, LPCWAVEFORMATEX f, DWORD_PTR cb, DWORD_PTR in, DWORD fdw) {
    ensureInit();
    if (!isMapper(id)) {
        std::vector<UINT> m = mapWaveIn();
        if (id < m.size()) id = m[id];
    }
    return r_wiOpen(ph, id, f, cb, in, fdw);
}

// For mixer calls, high nibble flags in fdw/uMxId mean the id is a handle or
// refers to another object type; in those cases we must not remap.
static bool mixerRaw(DWORD fdw) { return (fdw & 0xF0000000) != 0; }

UINT WINAPI mixerGetNumDevs(void) {
    ensureInit();
    return (UINT)mapMixer().size();
}
MMRESULT WINAPI mixerGetDevCapsA(UINT_PTR id, LPMIXERCAPSA p, UINT cb) {
    ensureInit();
    std::vector<UINT> m = mapMixer();
    if (id < m.size()) return r_mxCapsA(m[id], p, cb);
    return r_mxCapsA(id, p, cb); // out of our range -> pass through (handles etc.)
}
MMRESULT WINAPI mixerGetDevCapsW(UINT_PTR id, LPMIXERCAPSW p, UINT cb) {
    ensureInit();
    std::vector<UINT> m = mapMixer();
    if (id < m.size()) return r_mxCapsW(m[id], p, cb);
    return r_mxCapsW(id, p, cb);
}
MMRESULT WINAPI mixerOpen(LPHMIXER ph, UINT id, DWORD_PTR cb, DWORD_PTR in, DWORD fdw) {
    ensureInit();
    if (!mixerRaw(fdw)) {
        std::vector<UINT> m = mapMixer();
        if (id < m.size()) id = m[id];
    }
    return r_mxOpen(ph, id, cb, in, fdw);
}

} // extern "C"

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(h);
    return TRUE;
}
