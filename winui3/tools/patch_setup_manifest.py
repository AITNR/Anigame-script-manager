#!/usr/bin/env python3
"""把 PE 的清单改成 requireAdministrator —— 专用于 Inno 的编译器模板 SetupLdr.e32/e64。

背景：Inno 生成的 setup.exe 外层清单写死 asInvoker，PrivilegesRequired=admin 只是让
它「启动之后再 runas 一次」。想让安装包像主程序一样「双击即弹 UAC / 带盾牌图标」，
只能改引导器清单。改**编好的** setup.exe 做不到（见下），所以改**编译器模板**，
再由 ISCC 拿模板拼出真正带 requireAdministrator 的安装包。

⚠️ 绝对不要拿这个脚本去改编好的 setup.exe：
    Inno 的 setup.exe = PE 引导器 + 追加在文件尾的安装数据。
    mt.exe 与 Win32 的 UpdateResource 都会在写回时**丢掉整段追加数据** ——
    实测 75MB 的安装包被截成 1,000,448 字节，双击只会报「安装数据损坏」。
    本脚本因此先算「镜像结束位置」，只要发现文件尾部有追加数据就直接拒绝。

用法：
    # 改模板（CI 里在复制出来的编译器目录上做，不动 Program Files）
    python patch_setup_manifest.py "<inno 目录>/SetupLdr.e32" "<inno 目录>/SetupLdr.e64"

    # 只校验编好的安装包确实要求管理员（不改文件；CI 用来兜底）
    python patch_setup_manifest.py dist/Yomori-2.1.0-setup.exe --expect-admin

退出码：0 = 成功/符合预期；1 = 有文件失败或不符合预期。
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import os
import struct
import sys
from ctypes import wintypes

RT_MANIFEST = 24
AS_INVOKER = b'level="asInvoker"'
REQUIRE_ADMIN = b'level="requireAdministrator"'

# ── Win32 ────────────────────────────────────────────────────────────────
k32 = ctypes.WinDLL("kernel32", use_last_error=True)

k32.BeginUpdateResourceW.argtypes = [wintypes.LPCWSTR, wintypes.BOOL]
k32.BeginUpdateResourceW.restype = wintypes.HANDLE
k32.UpdateResourceW.argtypes = [
    # ⚠️ lpType / lpName 必须声明成 LPVOID 而不是 LPCWSTR：它们走的是 MAKEINTRESOURCE
    #    （把整数 ID 塞进指针位），声明成 LPCWSTR 时 ctypes 会拒绝 c_void_p
    #    （报 "argument 3: TypeError" —— 只在真正写盘时才触发，--expect-admin 查不出来）。
    wintypes.HANDLE, wintypes.LPVOID, wintypes.LPVOID, wintypes.WORD,
    wintypes.LPVOID, wintypes.DWORD]
k32.UpdateResourceW.restype = wintypes.BOOL
k32.EndUpdateResourceW.argtypes = [wintypes.HANDLE, wintypes.BOOL]
k32.EndUpdateResourceW.restype = wintypes.BOOL
k32.LoadLibraryExW.argtypes = [wintypes.LPCWSTR, wintypes.HANDLE, wintypes.DWORD]
k32.LoadLibraryExW.restype = wintypes.HMODULE
k32.FreeLibrary.argtypes = [wintypes.HMODULE]
k32.FreeLibrary.restype = wintypes.BOOL
k32.FindResourceW.argtypes = [wintypes.HMODULE, wintypes.LPVOID, wintypes.LPVOID]
k32.FindResourceW.restype = wintypes.HANDLE
k32.FindResourceExW.argtypes = [wintypes.HMODULE, wintypes.LPVOID, wintypes.LPVOID, wintypes.WORD]
k32.FindResourceExW.restype = wintypes.HANDLE
k32.LoadResource.argtypes = [wintypes.HMODULE, wintypes.HANDLE]
k32.LoadResource.restype = wintypes.HANDLE
k32.LockResource.argtypes = [wintypes.HANDLE]
k32.LockResource.restype = wintypes.LPVOID
k32.SizeofResource.argtypes = [wintypes.HMODULE, wintypes.HANDLE]
k32.SizeofResource.restype = wintypes.DWORD

ENUM_LANG_PROC = ctypes.WINFUNCTYPE(
    wintypes.BOOL, wintypes.HMODULE, wintypes.LPVOID, wintypes.LPVOID,
    wintypes.WORD, wintypes.LPARAM)
k32.EnumResourceLanguagesW.argtypes = [
    wintypes.HMODULE, wintypes.LPVOID, wintypes.LPVOID, ENUM_LANG_PROC, wintypes.LPARAM]
k32.EnumResourceLanguagesW.restype = wintypes.BOOL

LOAD_LIBRARY_AS_DATAFILE = 0x00000002
LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020


def make_int_resource(ident: int):
    """MAKEINTRESOURCE：把整数 ID 塞进指针参数（ctypes 的通用写法）。"""
    return ctypes.cast(ctypes.c_void_p(ident), wintypes.LPVOID)


def pe_overlay_offset(path: str) -> int:
    """返回 PE 镜像结束位置（= Inno 追加数据起点）的字节偏移。"""
    with open(path, "rb") as f:
        mz = f.read(0x40)
        if mz[:2] != b"MZ":
            raise SystemExit(f"{path}: 不是 PE 文件")
        e_lfanew = struct.unpack_from("<I", mz, 0x3C)[0]
        f.seek(e_lfanew)
        pe = f.read(24)
        if pe[:4] != b"PE\0\0":
            raise SystemExit(f"{path}: 缺少 PE 签名")
        n_sections = struct.unpack_from("<H", pe, 6)[0]
        opt_size = struct.unpack_from("<H", pe, 20)[0]
        f.seek(e_lfanew + 24 + opt_size)
        end = 0
        for _ in range(n_sections):
            s = f.read(40)
            raw_size, raw_ptr = struct.unpack_from("<II", s, 16)
            if raw_size:
                end = max(end, raw_ptr + raw_size)
        return end


def file_sha256(path: str, offset: int) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        f.seek(offset)
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def read_manifest(path: str) -> tuple[int, bytes]:
    """读出第一个 RT_MANIFEST 资源的 (语言 ID, 内容)。"""
    mod = k32.LoadLibraryExW(path, None, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE)
    if not mod:
        raise SystemExit(f"{path}: 打开资源失败（error {ctypes.get_last_error()}）")
    try:
        langs: list[int] = []

        def _cb(_h, _t, _n, lang, _p):
            langs.append(int(lang) & 0xFFFF)
            return True

        cb = ENUM_LANG_PROC(_cb)  # 必须留引用，别让回调被回收
        k32.EnumResourceLanguagesW(mod, make_int_resource(RT_MANIFEST),
                                   make_int_resource(1), cb, 0)
        if not langs:
            raise SystemExit(f"{path}: 没有 RT_MANIFEST 资源")

        # ⚠️ 必须用 FindResourceExW：Inno 的清单资源挂在 1033 下，
        #    语言中立的 FindResourceW 会因为「当前语言没有该资源」直接 1813 失败
        res = k32.FindResourceExW(mod, make_int_resource(RT_MANIFEST),
                                  make_int_resource(1), langs[0])
        if not res:
            raise SystemExit(f"{path}: FindResourceEx 失败（error {ctypes.get_last_error()}）")
        size = k32.SizeofResource(mod, res)
        ptr = k32.LockResource(k32.LoadResource(mod, res))
        if not ptr:
            raise SystemExit(f"{path}: 读取清单内容失败")
        return langs[0], ctypes.string_at(ptr, size)
    finally:
        k32.FreeLibrary(mod)


def check_admin(path: str) -> bool:
    """只校验：清单里必须是 requireAdministrator，且不能还有 asInvoker。"""
    lang, data = read_manifest(path)
    if REQUIRE_ADMIN in data and AS_INVOKER not in data:
        print(f"[ok] {path}: 清单要求管理员（语言 0x{lang:04x}）")
        return True
    print(f"[!] {path}: 清单不是 requireAdministrator（语言 0x{lang:04x}）", file=sys.stderr)
    return False


def patch(path: str, dry_run: bool) -> bool:
    before_size = os.path.getsize(path)
    overlay_off = pe_overlay_offset(path)

    # ⚠️ 有追加数据就先拒绝：UpdateResource / mt.exe 写回时会把整段追加数据丢掉。
    #    Inno 编好的 setup.exe 就是这样（尾部是安装数据），只有编译器模板
    #    SetupLdr.e32/e64 是干净的 PE。
    if overlay_off != before_size:
        print(f"[!] {path}: 文件尾部有 {before_size - overlay_off} 字节追加数据，"
              f"改它会丢数据 —— 请改 Inno 的 SetupLdr 模板，别改编好的安装包", file=sys.stderr)
        return False

    overlay_hash = file_sha256(path, overlay_off)

    lang, data = read_manifest(path)
    if REQUIRE_ADMIN in data:
        print(f"[ok] {path}: 清单已经是 requireAdministrator（语言 0x{lang:04x}），跳过")
        return True
    if data.count(AS_INVOKER) != 1:
        print(f"[!] {path}: 清单里 asInvoker 出现 {data.count(AS_INVOKER)} 次，不敢乱改", file=sys.stderr)
        return False

    new_data = data.replace(AS_INVOKER, REQUIRE_ADMIN)
    print(f"[..] {path}: 清单 {len(data)} → {len(new_data)} 字节（语言 0x{lang:04x}）")
    if dry_run:
        print(f"[dry] {path}: 不写盘")
        return True

    h = k32.BeginUpdateResourceW(path, False)
    if not h:
        print(f"[!] {path}: BeginUpdateResource 失败（error {ctypes.get_last_error()}）", file=sys.stderr)
        return False
    ok = k32.UpdateResourceW(h, make_int_resource(RT_MANIFEST), make_int_resource(1),
                             lang, new_data, len(new_data))
    if not ok:
        err = ctypes.get_last_error()
        k32.EndUpdateResourceW(h, True)  # 回滚
        print(f"[!] {path}: UpdateResource 失败（error {err}）", file=sys.stderr)
        return False
    if not k32.EndUpdateResourceW(h, False):
        print(f"[!] {path}: EndUpdateResource 失败（error {ctypes.get_last_error()}）", file=sys.stderr)
        return False

    # ── 自检：读回来必须真是 requireAdministrator，且仍然是「干净 PE」────────
    # 注意别断言「文件大小一点没变」：UpdateResource 会重排资源段，模板文件长几十字节
    # 是正常的；真正不能变的是「不能凭空长出追加数据」，以及清单得读得回来。
    after_size = os.path.getsize(path)
    problems = []
    if after_size < before_size:
        problems.append(f"文件被截短：{before_size} → {after_size}")
    after_off = pe_overlay_offset(path)
    if after_off != after_size:
        problems.append(f"改完尾部多了 {after_size - after_off} 字节追加数据")
    elif after_off != overlay_off or file_sha256(path, after_off) != overlay_hash:
        problems.append("追加数据被改动了 —— Inno 会报「安装数据损坏」")

    lang2, data2 = read_manifest(path)
    if REQUIRE_ADMIN not in data2:
        problems.append("写回后清单里读不到 requireAdministrator")
    if AS_INVOKER in data2:
        problems.append("写回后清单里仍有 asInvoker")

    if problems:
        for p in problems:
            print(f"[!] {path}: {p}", file=sys.stderr)
        return False

    print(f"[ok] {path}: requireAdministrator 写入成功（{before_size} → {after_size} 字节，"
          f"无追加数据）")
    return True


def main() -> int:
    ap = argparse.ArgumentParser(description="把 PE 清单改成 requireAdministrator（用于 Inno 的 SetupLdr 模板）")
    ap.add_argument("files", nargs="+", help="PE 文件路径（可多个）")
    ap.add_argument("--dry-run", action="store_true", help="只检查并打印，不写盘")
    ap.add_argument("--expect-admin", action="store_true",
                    help="不改文件，只断言清单要求管理员（校验编好的安装包用）")
    args = ap.parse_args()

    if args.expect_admin:
        failed = [f for f in args.files if not check_admin(f)]
    else:
        failed = [f for f in args.files if not patch(f, args.dry_run)]

    if failed:
        print(f"\n失败 {len(failed)} 个：{', '.join(failed)}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
