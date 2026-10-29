#include <windows.h>
#include <cstdio>
#include <string>
#include <vector>

// Runs only the tiny managed diagnostic inside the game's Mono runtime.
// Does not load UnityPlayer.dll, start the game executable or access saves.
static std::string utf8(const wchar_t* value) {
    int size = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    std::vector<char> data(size);
    WideCharToMultiByte(CP_UTF8, 0, value, -1, data.data(), size, nullptr, nullptr);
    return data.data();
}

template<class T> static T symbol(HMODULE library, const char* name) {
    auto address = GetProcAddress(library, name);
    if (!address) { std::fprintf(stderr, "Missing Mono export: %s\n", name); ExitProcess(2); }
    return reinterpret_cast<T>(address);
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 4 && argc != 6) {
        std::fprintf(stderr, "Usage: mono-probe.exe <Mono dll> <game Managed directory> <probe exe> [candidate folder fingerprint]\n");
        return 2;
    }
    auto library = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!library) { std::fprintf(stderr, "Cannot load Mono: Win32=%lu\n", GetLastError()); return 2; }
    auto set_path = symbol<void(__cdecl*)(const char*)>(library, "mono_set_assemblies_path");
    auto init = symbol<void*(__cdecl*)(const char*, const char*)>(library, "mono_jit_init_version");
    auto open = symbol<void*(__cdecl*)(void*, const char*)>(library, "mono_domain_assembly_open");
    auto exec = symbol<int(__cdecl*)(void*, void*, int, char**)>(library, "mono_jit_exec");
    auto cleanup = symbol<void(__cdecl*)(void*)>(library, "mono_jit_cleanup");
    auto managed = utf8(argv[2]);
    auto probe = utf8(argv[3]);
    set_path(managed.c_str());
    auto domain = init("WOTR-Offline-Harmony-Probe", "v4.0.30319");
    if (!domain) { std::fprintf(stderr, "Cannot initialize standalone Mono\n"); return 2; }
    auto assembly = open(domain, probe.c_str());
    if (!assembly) { std::fprintf(stderr, "Cannot open diagnostic assembly\n"); cleanup(domain); return 2; }
    std::vector<std::string> values { probe };
    for (int i = 4; i < argc; ++i) values.push_back(utf8(argv[i]));
    std::vector<char*> args;
    for (auto& value : values) args.push_back(&value[0]);
    auto result = exec(domain, assembly, static_cast<int>(args.size()), args.data());
    cleanup(domain);
    return result;
}
