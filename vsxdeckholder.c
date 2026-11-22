/* VSX Deck Holder: a VST2 plugin that wraps Slate VSX (VST2) and keeps ONE instance alive while the Stream Deck
 * switches its settings, so switching never re-creates VSX (no volume burst). Used by VSX Deck (system audio)
 * and in a DAW (e.g. Reaper's Monitoring FX).
 *
 * Deck state lives in %LOCALAPPDATA%\VSX Deck\ (per user) and is polled every 50 ms:
 *   active.vsx  VSX state ("VC2!..."): applied to the live VSX. Empty = OFF: 30 ms fade to dry, VSX keeps running.
 *   bypass.txt  "1"/"0": VSX's own level-matched bypass (a flag in its state), kept across room changes.
 *   save.req    "<slot>": the instance whose window is open stores its settings as slot<N>.vsx (un-bypassed).
 * What this instance applied is reported in status.txt: "on <ROOM>\nbypass 0|1" or "off".
 *
 * The editor is VSX's own window inside a container. VSX only reads its settings when its window opens, so after
 * a change we reopen it behind a snapshot of the old window and drop the snapshot once VSX has redrawn.
 *
 * Build: cl /O2 /LD vsxdeckholder.c user32.lib gdi32.lib /Fe:"VSX Deck Holder.dll"
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define VSX_DLL L"C:\\Program Files\\VstPlugins\\VSX.dll"
#define FADE_SECONDS 0.03f
#define POLL_MS 50
#define MAX_STATE (1 << 20)   /* largest deck file we read */

/* ---- minimal VST2 ABI ------------------------------------------------------------------------------- */
typedef struct AEffect AEffect;
typedef intptr_t (*HostCb)(AEffect*, int32_t, int32_t, intptr_t, void*, float);
typedef intptr_t (*DispatchFn)(AEffect*, int32_t, int32_t, intptr_t, void*, float);
typedef void (*ProcessFn)(AEffect*, float**, float**, int32_t);
typedef void (*ProcessDblFn)(AEffect*, double**, double**, int32_t);
typedef void (*SetParamFn)(AEffect*, int32_t, float);
typedef float (*GetParamFn)(AEffect*, int32_t);
struct AEffect {
    int32_t magic; DispatchFn dispatcher; ProcessFn process; SetParamFn setParameter; GetParamFn getParameter;
    int32_t numPrograms, numParams, numInputs, numOutputs, flags; intptr_t resvd1, resvd2;
    int32_t initialDelay, realQualities, offQualities; float ioRatio; void *object, *user;
    int32_t uniqueID, version; ProcessFn processReplacing; ProcessDblFn processDoubleReplacing; char future[56];
};
enum { effOpen = 0, effClose = 1, effSetSampleRate = 10, effSetBlockSize = 11, effEditGetRect = 13, effEditOpen = 14,
       effEditClose = 15, effGetChunk = 23, effSetChunk = 24, effGetEffectName = 45, effGetVendorString = 47,
       effGetProductString = 48 };
typedef AEffect* (*PluginMain)(HostCb);

typedef struct { FILETIME time; DWORD size; int known, exists; } Seen;

typedef struct {
    AEffect outer;                  /* what the host sees; must stay first */
    AEffect *inner;                 /* the real VSX */
    HMODULE lib;
    HANDLE thread, stop;            /* deck-state watcher */
    volatile LONG wet;              /* 1 = VSX, 0 = dry (OFF) */
    float gain, step;               /* current wet gain, per-sample fade step */
    float *dry[8]; int32_t cap;
    Seen active, bypassFile, save;
    int bypass;                     /* wanted level-matched bypass */
    volatile LONG editorOpen;       /* only an instance whose window is open answers Save */
    HWND container, cover;          /* editor: container holding VSX's window, and the snapshot cover */
    HBITMAP coverBmp;
    DWORD coverSince; int stable; long long lastSig;
} Hold;

static HostCb g_host;
static AEffect *g_creating;         /* our AEffect while VSX is being created (it calls the host back early) */
static HWND g_msg;                  /* message window on VSX's thread (the host thread that created it) */
static DWORD g_msgThread;

/* ---- calls into VSX: always on the thread that created it ---------------------------------------------
 * VSX (JUCE) expects non-audio calls on that thread; the watcher thread hands them over. */
typedef struct { Hold *h; int32_t op, idx; intptr_t v; void *p; float o; intptr_t r; } Call;
static LRESULT CALLBACK msgProc(HWND w, UINT m, WPARAM wp, LPARAM lp) {
    if (m == WM_APP) { Call *c = (Call*)lp; c->r = c->h->inner->dispatcher(c->h->inner, c->op, c->idx, c->v, c->p, c->o); return 0; }
    return DefWindowProcW(w, m, wp, lp);
}
static intptr_t vsx(Hold *h, int32_t op, int32_t idx, intptr_t v, void *p, float o) {
    if (GetCurrentThreadId() == g_msgThread) return h->inner->dispatcher(h->inner, op, idx, v, p, o);
    Call c = { h, op, idx, v, p, o, 0 };
    SendMessageW(g_msg, WM_APP, 0, (LPARAM)&c);
    return c.r;
}
static intptr_t innerHost(AEffect *e, int32_t op, int32_t idx, intptr_t v, void *p, float o) {
    AEffect *outer = (e && e->user) ? (AEffect*)e->user : g_creating;   /* hand the host our AEffect, not VSX's */
    if (op == 1 /* audioMasterVersion */ && !outer) return 2400;
    return g_host ? g_host(outer, op, idx, v, p, o) : 0;
}

/* ---- deck state files -------------------------------------------------------------------------------- */
static void dataPath(wchar_t *buf, const wchar_t *name) {   /* per-user: only this user can write the deck state */
    DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH);
    if (!n || n > MAX_PATH - 64) { buf[0] = 0; return; }       /* unusable: every file operation then just fails */
    wcscat_s(buf, MAX_PATH, L"\\VSX Deck\\");
    wcscat_s(buf, MAX_PATH, name);
}
static int changed(const wchar_t *name, Seen *s) {   /* created, deleted or rewritten since last call? */
    wchar_t p[MAX_PATH]; WIN32_FILE_ATTRIBUTE_DATA a;
    dataPath(p, name);
    int exists = GetFileAttributesExW(p, GetFileExInfoStandard, &a);
    if (s->known && exists == s->exists && (!exists || (a.nFileSizeLow == s->size && !CompareFileTime(&a.ftLastWriteTime, &s->time)))) return 0;
    s->known = 1; s->exists = exists;
    if (exists) { s->time = a.ftLastWriteTime; s->size = a.nFileSizeLow; }
    return 1;
}
static char *readFile(const wchar_t *name, DWORD *size) {   /* NUL-terminated copy, or NULL */
    wchar_t p[MAX_PATH]; dataPath(p, name);
    HANDLE f = CreateFileW(p, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return NULL;
    DWORD n = GetFileSize(f, NULL), got = 0;
    if (n > MAX_STATE) { CloseHandle(f); return NULL; }        /* VSX states are ~1 KB; refuse anything odd */
    char *b = (char*)malloc((size_t)n + 1);
    if (b && !(ReadFile(f, b, n, &got, NULL) && got == n)) { free(b); b = NULL; }
    CloseHandle(f);
    if (b) { b[n] = 0; *size = n; }
    return b;
}
static void writeFile(const wchar_t *name, const void *data, DWORD size) {   /* atomic: readers never see half */
    wchar_t p[MAX_PATH], tmp[MAX_PATH];
    dataPath(p, name); wcscpy_s(tmp, MAX_PATH, p); wcscat_s(tmp, MAX_PATH, L".tmp");
    HANDLE f = CreateFileW(tmp, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return;
    DWORD w = 0; BOOL ok = WriteFile(f, data, size, &w, NULL) && w == size;
    CloseHandle(f);
    if (ok) MoveFileExW(tmp, p, MOVEFILE_REPLACE_EXISTING);
}

/* ---- VSX state ("VC2!" + size + XML) ------------------------------------------------------------------- */
static int isState(const char *b, DWORD n) { return b && n > 8 && !memcmp(b, "VC2!", 4); }
static char *attr(const char *b, DWORD n, const char *key) {   /* value of key="..." in the state XML */
    size_t k = strlen(key);
    for (DWORD i = 0; b && i + k < n; i++) if (!memcmp(b + i, key, k)) return (char*)b + i + k;
    return NULL;
}
static void setBypass(char *b, DWORD n, int on) { char *v = attr(b, n, "m_currentBypassState=\""); if (v) *v = on ? '1' : '0'; }
static int isBypassed(const char *b, DWORD n) { const char *v = attr(b, n, "m_currentBypassState=\""); return v && *v == '1'; }
static char *liveState(Hold *h, DWORD *n) {   /* copy of VSX's current state (VSX owns its buffer) */
    void *live = NULL;
    intptr_t size = vsx(h, effGetChunk, 0, 0, &live, 0.f);
    if (!isState((const char*)live, (DWORD)size)) return NULL;
    char *b = (char*)malloc((size_t)size);
    if (b) { memcpy(b, live, (size_t)size); *n = (DWORD)size; }
    return b;
}
static void writeStatus(const char *state, DWORD n) {   /* NULL = off */
    char s[160] = "off";
    if (state) {
        const char *room = attr(state, n, "m_currentStudioName=\"");
        int len = 0;
        while (room && len < 100 && room + len < state + n && room[len] != '"') len++;
        sprintf_s(s, sizeof s, "on %.*s\nbypass %d", len, room ? room : "", isBypassed(state, n));
    }
    writeFile(L"status.txt", s, (DWORD)strlen(s));
}

static void editorChanged(Hold *h);
static void apply(Hold *h, char *state, DWORD n) {   /* same instance, new settings */
    setBypass(state, n, h->bypass);                    /* bypass survives room changes */
    vsx(h, effSetChunk, 0, (intptr_t)n, state, 0.f);
    InterlockedExchange(&h->wet, 1);
    writeStatus(state, n);
    editorChanged(h);
}
static void pollActive(Hold *h) {
    if (!changed(L"active.vsx", &h->active)) return;
    DWORD n = 0;
    char *b = h->active.exists ? readFile(L"active.vsx", &n) : NULL;
    if (h->active.exists && !b) { h->active.known = 0; return; }   /* being written: retry next poll */
    if (isState(b, n)) apply(h, b, n);
    else { InterlockedExchange(&h->wet, 0); writeStatus(NULL, 0); }
    free(b);
}
static void pollBypass(Hold *h) {
    if (!changed(L"bypass.txt", &h->bypassFile)) return;
    DWORD n = 0; char *b = readFile(L"bypass.txt", &n);
    int want = b && b[0] == '1';
    free(b);
    if (want == h->bypass) return;
    h->bypass = want;
    if (!h->wet) return;                                /* applied with the next preset */
    char *s = liveState(h, &n);
    if (s) { apply(h, s, n); free(s); }
}
static void pollSave(Hold *h) {
    if (!changed(L"save.req", &h->save) || !h->save.exists || !h->editorOpen) return;
    DWORD n = 0; char *b = readFile(L"save.req", &n);
    int slot = b ? atoi(b) : 0;
    free(b);
    char *s = slot >= 1 && slot <= 10 ? liveState(h, &n) : NULL;
    if (!s) return;
    int live = isBypassed(s, n);
    setBypass(s, n, 0);                                 /* presets are stored un-bypassed */
    wchar_t name[16]; swprintf_s(name, 16, L"slot%d.vsx", slot);
    writeFile(name, s, n);
    char num[4]; int len = sprintf_s(num, sizeof num, "%d", slot);
    writeFile(L"lastslot.txt", num, (DWORD)len);
    writeFile(L"active.vsx", s, n);                     /* other instances follow; this one already runs it */
    changed(L"active.vsx", &h->active);
    InterlockedExchange(&h->wet, 1);
    setBypass(s, n, live);
    writeStatus(s, n);
    free(s);
}
static DWORD WINAPI watcher(void *arg) {
    Hold *h = (Hold*)arg;
    while (WaitForSingleObject(h->stop, POLL_MS) == WAIT_TIMEOUT) { pollBypass(h); pollActive(h); pollSave(h); }
    return 0;
}

/* ---- audio: VSX always runs; OFF fades to the dry input ---------------------------------------------- */
static void ensureDry(Hold *h, int32_t n) {
    if (n <= h->cap) return;
    for (int c = 0; c < 8; c++) { free(h->dry[c]); h->dry[c] = (float*)calloc((size_t)n, sizeof(float)); }
    h->cap = n;
}
static void process(AEffect *e, float **in, float **out, int32_t n) {
    Hold *h = (Hold*)e;
    int chs = e->numOutputs < e->numInputs ? e->numOutputs : e->numInputs;
    if (chs > 8) chs = 8;
    ensureDry(h, n);
    for (int c = 0; c < chs; c++) memcpy(h->dry[c], in[c], (size_t)n * sizeof(float));   /* in may alias out */
    h->inner->processReplacing(h->inner, in, out, n);
    float t = (float)h->wet, g = h->gain;
    if (g == 1.f && t == 1.f) return;
    for (int32_t i = 0; i < n; i++) {
        g = g < t ? (g + h->step > t ? t : g + h->step) : (g - h->step < t ? t : g - h->step);
        for (int c = 0; c < chs; c++) out[c][i] = h->dry[c][i] + (out[c][i] - h->dry[c][i]) * g;
    }
    h->gain = g;
}

/* ---- editor: VSX's window, reopened behind a snapshot after each change --------------------------------- */
#define WM_REFRESH (WM_APP + 1)
static HWND vsxWindow(Hold *h) {
    for (HWND c = GetWindow(h->container, GW_CHILD); c; c = GetWindow(c, GW_HWNDNEXT)) if (c != h->cover) return c;
    return NULL;
}
static HBITMAP snapshot(HWND w, int *wd, int *ht, uint32_t **px) {
    RECT r; GetClientRect(w, &r); *wd = r.right; *ht = r.bottom;
    if (*wd <= 0 || *ht <= 0) return NULL;
    BITMAPINFO bi = { { sizeof(BITMAPINFOHEADER), *wd, -*ht, 1, 32, BI_RGB } };
    HDC screen = GetDC(NULL), mem = CreateCompatibleDC(screen);
    HBITMAP b = CreateDIBSection(screen, &bi, DIB_RGB_COLORS, (void**)px, NULL, 0);
    HGDIOBJ old = SelectObject(mem, b);
    PrintWindow(w, mem, 2 /* PW_RENDERFULLCONTENT */);
    SelectObject(mem, old); DeleteDC(mem); ReleaseDC(NULL, screen);
    return b;
}
static long long signature(const uint32_t *px, int w, int h) {   /* sampled grid, ignoring the level meters */
    long long s = 17;
    for (int y = 4; y < h; y += h / 24 + 1)
        for (int x = 4; x < w; x += w / 32 + 1)
            if (!(x > w * 0.74 && y > h * 0.80)) s = s * 31 + px[y * w + x];
    return s;
}
static void refreshEditor(Hold *h) {
    HWND v = vsxWindow(h);
    int w, ht; uint32_t *px;
    HBITMAP b = v && IsWindowVisible(h->container) ? snapshot(v, &w, &ht, &px) : NULL;
    if (b) {
        if (h->coverBmp) DeleteObject(h->coverBmp);
        h->coverBmp = b;
        SetWindowPos(h->cover, HWND_TOP, 0, 0, w, ht, SWP_SHOWWINDOW);
        InvalidateRect(h->cover, NULL, FALSE); UpdateWindow(h->cover);
    }
    h->inner->dispatcher(h->inner, effEditClose, 0, 0, NULL, 0.f);
    h->inner->dispatcher(h->inner, effEditOpen, 0, 0, h->container, 0.f);
    if (b) {
        SetWindowPos(h->cover, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);   /* above the new VSX window */
        h->coverSince = GetTickCount(); h->stable = 0; h->lastSig = 0;
        SetTimer(h->container, 1, 30, NULL);
    }
}
static void settleTick(Hold *h) {   /* drop the cover once VSX's window stops changing (~90 ms) */
    HWND v = vsxWindow(h);
    int w, ht; uint32_t *px;
    HBITMAP b = v ? snapshot(v, &w, &ht, &px) : NULL;
    long long sig = b ? signature(px, w, ht) : 0;
    if (b) DeleteObject(b);
    h->stable = sig == h->lastSig ? h->stable + 1 : 0; h->lastSig = sig;
    DWORD age = GetTickCount() - h->coverSince;
    if ((age > 120 && h->stable >= 3) || age > 2000) { KillTimer(h->container, 1); ShowWindow(h->cover, SW_HIDE); }
}
static LRESULT CALLBACK containerProc(HWND w, UINT m, WPARAM wp, LPARAM lp) {
    Hold *h = (Hold*)GetWindowLongPtrW(w, GWLP_USERDATA);
    if (h && m == WM_REFRESH) { refreshEditor(h); return 0; }
    if (h && m == WM_TIMER && wp == 1) { settleTick(h); return 0; }
    if (h && m == WM_PAINT && w == h->cover && h->coverBmp) {
        PAINTSTRUCT ps; HDC dc = BeginPaint(w, &ps), mem = CreateCompatibleDC(dc);
        HGDIOBJ o = SelectObject(mem, h->coverBmp); RECT r; GetClientRect(w, &r);
        BitBlt(dc, 0, 0, r.right, r.bottom, mem, 0, 0, SRCCOPY);
        SelectObject(mem, o); DeleteDC(mem); EndPaint(w, &ps);
        return 0;
    }
    if (m == WM_ERASEBKGND) return 1;
    return DefWindowProcW(w, m, wp, lp);
}
static intptr_t openEditor(Hold *h, HWND parent) {
    HINSTANCE inst = GetModuleHandleW(NULL);
    WNDCLASSW wc = { 0 }; wc.lpfnWndProc = containerProc; wc.hInstance = inst; wc.lpszClassName = L"VSXDeckEditor";
    wc.hCursor = LoadCursor(NULL, IDC_ARROW);
    RegisterClassW(&wc);   /* fails harmlessly when already registered */
    short *rect = NULL; int w = 800, ht = 500;
    h->inner->dispatcher(h->inner, effEditGetRect, 0, 0, &rect, 0.f);
    if (rect) { ht = rect[2] - rect[0]; w = rect[3] - rect[1]; }   /* top, left, bottom, right */
    h->container = CreateWindowExW(0, L"VSXDeckEditor", L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, w, ht, parent, NULL, inst, NULL);
    h->cover = CreateWindowExW(0, L"VSXDeckEditor", L"", WS_CHILD, 0, 0, w, ht, h->container, NULL, inst, NULL);
    SetWindowLongPtrW(h->container, GWLP_USERDATA, (LONG_PTR)h);
    SetWindowLongPtrW(h->cover, GWLP_USERDATA, (LONG_PTR)h);
    intptr_t r = h->inner->dispatcher(h->inner, effEditOpen, 0, 0, h->container, 0.f);
    InterlockedExchange(&h->editorOpen, 1);
    return r;
}
static intptr_t closeEditor(Hold *h) {
    InterlockedExchange(&h->editorOpen, 0);
    intptr_t r = h->inner->dispatcher(h->inner, effEditClose, 0, 0, NULL, 0.f);
    if (h->container) { KillTimer(h->container, 1); SetWindowLongPtrW(h->container, GWLP_USERDATA, 0); DestroyWindow(h->container); }
    h->container = h->cover = NULL;
    if (h->coverBmp) { DeleteObject(h->coverBmp); h->coverBmp = NULL; }
    return r;
}
static void editorChanged(Hold *h) { HWND c = h->container; if (c) PostMessageW(c, WM_REFRESH, 0, 0); }

/* ---- plugin ------------------------------------------------------------------------------------------- */
static void setParam(AEffect *e, int32_t i, float v) { ((Hold*)e)->inner->setParameter(((Hold*)e)->inner, i, v); }
static float getParam(AEffect *e, int32_t i) { return ((Hold*)e)->inner->getParameter(((Hold*)e)->inner, i); }

static intptr_t dispatch(AEffect *e, int32_t op, int32_t idx, intptr_t v, void *p, float o) {
    Hold *h = (Hold*)e;
    switch (op) {
    case effGetEffectName: case effGetProductString: if (p) strcpy_s((char*)p, 32, "VSX Deck Holder"); return 1;
    case effGetVendorString: if (p) strcpy_s((char*)p, 64, "VSX Deck"); return 1;
    case effEditOpen: return openEditor(h, (HWND)p);
    case effEditClose: return closeEditor(h);
    case effSetSampleRate: if (o > 0) h->step = 1.f / (FADE_SECONDS * o); break;
    case effSetBlockSize: if (v > 0) ensureDry(h, (int32_t)v); break;
    case effClose: {
        SetEvent(h->stop);
        /* the watcher may be inside SendMessage to this very thread: keep serving sent messages */
        while (MsgWaitForMultipleObjects(1, &h->thread, FALSE, INFINITE, QS_SENDMESSAGE) == WAIT_OBJECT_0 + 1) {
            MSG m; PeekMessageW(&m, NULL, 0, 0, PM_NOREMOVE);
        }
        CloseHandle(h->thread); CloseHandle(h->stop);
        intptr_t r = h->inner->dispatcher(h->inner, effClose, 0, 0, NULL, 0.f);
        FreeLibrary(h->lib);
        for (int c = 0; c < 8; c++) free(h->dry[c]);
        free(h);
        return r;
    }
    }
    intptr_t r = h->inner->dispatcher(h->inner, op, idx, v, p, o);
    if (op == effSetChunk) h->active.known = 0;          /* host restoring a project: the deck's state wins */
    if (op == effOpen) {
        changed(L"save.req", &h->save);                  /* ignore save requests older than us */
        pollBypass(h);                                   /* wanted bypass first, so the preset gets it */
        pollActive(h);                                   /* start in the deck's preset, no fade */
        h->gain = (float)h->wet;
        h->stop = CreateEventW(NULL, TRUE, FALSE, NULL);
        h->thread = CreateThread(NULL, 0, watcher, h, 0, NULL);
    }
    return r;
}

__declspec(dllexport) AEffect *VSTPluginMain(HostCb host) {
    g_host = host;
    if (!g_msg) {                                        /* VSX's thread = the host thread creating plugins */
        WNDCLASSW wc = { 0 }; wc.lpfnWndProc = msgProc; wc.hInstance = GetModuleHandleW(NULL); wc.lpszClassName = L"VSXDeckMsg";
        RegisterClassW(&wc);
        g_msgThread = GetCurrentThreadId();
        g_msg = CreateWindowW(wc.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, NULL, wc.hInstance, NULL);
    }
    Hold *h = (Hold*)calloc(1, sizeof(Hold));
    if (!h) return NULL;
    h->lib = LoadLibraryExW(VSX_DLL, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    PluginMain pm = h->lib ? (PluginMain)GetProcAddress(h->lib, "VSTPluginMain") : NULL;
    g_creating = &h->outer;
    AEffect *in = pm ? pm(innerHost) : NULL;
    g_creating = NULL;
    if (!in || in->magic != 0x56737450 /* 'VstP' */) { if (h->lib) FreeLibrary(h->lib); free(h); return NULL; }
    in->user = &h->outer;                                /* VSX's host calls get our AEffect */

    h->inner = in; h->step = 1.f / (FADE_SECONDS * 48000.f);
    h->outer = *in;                                      /* same channels, programs, latency, flags */
    h->outer.dispatcher = dispatch;
    h->outer.process = h->outer.processReplacing = process;
    h->outer.processDoubleReplacing = NULL;
    h->outer.flags &= ~(1 << 12);                        /* effFlagsCanDoubleReplacing: float only */
    h->outer.setParameter = setParam;
    h->outer.getParameter = getParam;
    h->outer.object = h->outer.user = NULL;
    h->outer.uniqueID = 0x56534448;                      /* 'VSDH': our own ID, distinct from VSX */
    return &h->outer;
}
