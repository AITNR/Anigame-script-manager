# -*- coding: utf-8 -*-
"""把构建产物收拢成一个可以直接解压运行的发布包。

为什么需要它（CI 与本机都用同一份逻辑）：

1. **`dotnet publish` 的输出不完整**。WinUI 3 的 `YukinoChan.pri` 与各页面的 `*.xbf`
   （`Views/`、`Themes/`、`embed/`）只在 **build** 输出里，publish 目录里没有；
   `YukinoChan.RdpNative` 那批 FreeRDP 运行库（freerdp3 / winpr3 / openssl / zlib…）
   也只在 build 输出里。缺了它们，程序要么起不来、要么页面打不开 ——
   所以打包 = publish 输出（自包含时，带 .NET 运行时）+ build 输出（补齐运行期文件）。
2. **绝不能把仓库根的 `config.json` 打进包**：那是使用者本机的真实配置
   （目标账户名、桥目录等）。程序首次运行会自己生成默认配置。
3. 看板娘素材在**仓库根** `assets/`，必须与 exe 同级落地 ——
   `AppPaths.ResolveBaseDir()` 就是靠"同级有 assets/ 或 config.json"回溯定位程序根目录的。

用法：

    # 框架依赖（体积小，需要目标机器装 .NET 8 运行时）
    python winui3/tools/package_release.py --build-dir <build 输出> \
        --assets assets --out dist --name YukinoChan-winui3-2.1.0-x64

    # 自包含（解压即用）
    python winui3/tools/package_release.py --build-dir <build 输出> --publish-dir <publish 输出> ...

打包完成后会做**自检**：关键文件缺任何一个就非零退出（宁可 CI 红，也别发出一个跑不起来的包）。
"""

from __future__ import annotations

import argparse
import shutil
import sys
import zipfile
from pathlib import Path

# ---------------------------------------------------------------- 文件筛选

# build 输出里的"构建过程垃圾"：不进发布包
_JUNK_SUFFIXES = (
    ".cache",
    ".g.cs",
    ".g.i.cs",
    ".resfiles",
    ".editorconfig",
    ".sourcelink.json",
    ".filelistabsolute.txt",
    ".up2date",
)

_JUNK_NAMES = {
    "input.json",
    "output.json",
    "xamlsavestatefile.xml",
    "multiplequalifiersperdimensionfound.txt",
    "yukinochan.app.assemblyinfo.cs",
    "yukinochan.app.assemblyinfoinputs.cache",
}

# build 输出里的中间目录：不进发布包
_JUNK_DIRS = {"ref", "refint", "obj", ".vs", "win-x64", "bin"}


def _is_junk(rel: Path) -> bool:
    """rel 是相对 build 输出根的路径。"""
    if any(part.lower() in _JUNK_DIRS for part in rel.parts[:-1]):
        return True

    name = rel.name.lower()
    if name in _JUNK_NAMES:
        return True
    if name.endswith(_JUNK_SUFFIXES):
        return True
    # XAML 源码副本（真正生效的是 *.xbf，源码拷进去只是噪音）
    if name.endswith(".xaml"):
        return True
    if name.endswith(".pdb"):
        return True  # 需要符号时用 --keep-pdb

    return False


def _copy_tree(src: Path, dst: Path, *, skip_junk: bool, keep_pdb: bool = False) -> int:
    """把 src 目录内容拷进 dst（合并覆盖），返回拷贝的文件数。"""
    if not src.is_dir():
        raise SystemExit(f"目录不存在：{src}")

    count = 0
    for item in src.rglob("*"):
        rel = item.relative_to(src)
        if item.is_dir():
            continue
        if skip_junk and _is_junk(rel) and not (keep_pdb and rel.name.lower().endswith(".pdb")):
            continue

        target = dst / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(item, target)
        count += 1

    return count


# ---------------------------------------------------------------- 自检

def _verify(app_dir: Path, *, require_dotnet_runtime: bool) -> None:
    """关键文件自检：缺任何一个都算打包失败。"""
    problems: list[str] = []

    def need(rel: str, note: str) -> None:
        if not (app_dir / rel).exists():
            problems.append(f"{rel}（{note}）")

    need("YukinoChan.exe", "主程序")
    need("YukinoChan.dll", "主程序集")
    need("YukinoChan.pri", "XAML 资源索引 —— 缺了界面打不开")
    need("YukinoChan.runtimeconfig.json", ".NET 运行时配置")
    need("ycn_rdp.dll", "内嵌 RDP 原生层")
    need("freerdp3.dll", "FreeRDP 运行库")
    need("winpr3.dll", "WinPR 运行库")
    need("assets/mascot", "看板娘素材（也是程序根目录的定位标记）")

    xbf = list(app_dir.glob("Views/*.xbf"))
    if not xbf:
        problems.append("Views/*.xbf（页面 XAML 编译产物）")

    if require_dotnet_runtime and not (app_dir / "System.Private.CoreLib.dll").exists():
        problems.append("System.Private.CoreLib.dll（自包含模式应带 .NET 运行时）")

    if (app_dir / "config.json").exists():
        problems.append("config.json（不该出现在发布包里 —— 会泄露使用者本机配置）")

    if problems:
        print("打包自检未通过，缺少/多出：", file=sys.stderr)
        for p in problems:
            print("  - " + p, file=sys.stderr)
        raise SystemExit(1)


# ---------------------------------------------------------------- 说明文件

_HELP_TEXT = """\
雪乃酱 / 二游脚本助手 · WinUI 3 版
版本：{version}
打包时间：{built_at}

────────────────────────────────────────
怎么用
────────────────────────────────────────
1. 解压到一个**固定目录**（不要放在临时目录里，日志与统计会写在它旁边）；
2. 双击 YukinoChan.exe —— 首次运行会自动生成 config.json（默认配置）；
3. 要接远程会话，先在左侧菜单「会话通道 → 通道管理…」里配置通道：
   目标主机 / 目标账户 / 保存凭据 / 开启远程桌面 / 部署会话代理 / 重新预检。
   详细步骤见仓库里的 winui3/USER_GUIDE.md。

────────────────────────────────────────
这个包是什么
────────────────────────────────────────
{package_kind}

目录约定：
  assets/          看板娘与卡片素材（删掉会导致程序找不到自己的根目录）
  logs/            运行日志（首次运行后生成）
  runtime_stats/   耗时统计（首次运行后生成）
  config.json      你的配置，**不随包发布**（首次运行自动生成）

────────────────────────────────────────
注意
────────────────────────────────────────
· WinUI 3 内嵌画面依赖 D3D11，远程桌面/虚拟机里可能需要图形加速；
· 程序本体只支持 Windows 10 1809 及以上、x64。
"""


def _write_help(app_dir: Path, version: str, package_kind: str, built_at: str) -> None:
    (app_dir / "使用说明.txt").write_text(
        _HELP_TEXT.format(version=version, package_kind=package_kind, built_at=built_at),
        encoding="utf-8-sig",  # 记事本打开不乱码
    )


# ---------------------------------------------------------------- 主流程

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="收拢 WinUI 3 发布包")
    parser.add_argument("--build-dir", required=True, help="dotnet build 的输出目录")
    parser.add_argument("--publish-dir", help="dotnet publish 的输出目录（自包含时给）")
    parser.add_argument("--assets", required=True, help="仓库根的 assets 目录")
    parser.add_argument("--out", required=True, help="输出目录（会生成 <out>/app 与 <out>/<name>.zip）")
    parser.add_argument("--name", required=True, help="ZIP 文件名（不含扩展名）")
    parser.add_argument("--version", default="0.0.0", help="写进说明文件的版本号")
    parser.add_argument("--keep-pdb", action="store_true", help="保留 pdb 符号（排查崩溃用）")
    parser.add_argument("--no-zip", action="store_true", help="只铺目录，不压缩（本地调试用）")
    args = parser.parse_args(argv)

    build_dir = Path(args.build_dir).resolve()
    publish_dir = Path(args.publish_dir).resolve() if args.publish_dir else None
    assets_dir = Path(args.assets).resolve()
    out_dir = Path(args.out).resolve()
    app_dir = out_dir / "app"

    for path, label in ((build_dir, "build 输出"), (assets_dir, "assets")):
        if not path.is_dir():
            raise SystemExit(f"{label}目录不存在：{path}")
    if publish_dir is not None and not publish_dir.is_dir():
        raise SystemExit(f"publish 输出目录不存在：{publish_dir}")

    if app_dir.exists():
        shutil.rmtree(app_dir)
    app_dir.mkdir(parents=True)

    copied = 0
    if publish_dir is not None:
        copied += _copy_tree(publish_dir, app_dir, skip_junk=False)
    copied += _copy_tree(build_dir, app_dir, skip_junk=True, keep_pdb=args.keep_pdb)

    # 看板娘素材：必须在 exe 同级
    copied += _copy_tree(assets_dir, app_dir / "assets", skip_junk=False)

    import datetime

    built_at = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
    package_kind = (
        "自包含包（.NET 运行时与 Windows App SDK 都已内置）—— 解压即可运行，不需要额外安装任何东西。"
        if publish_dir is not None
        else "框架依赖包 —— 需要目标机器已安装 .NET 8 运行时（Desktop Runtime 或 SDK 均可）。"
    )
    _write_help(app_dir, args.version, package_kind, built_at)

    _verify(app_dir, require_dotnet_runtime=publish_dir is not None)

    total_mb = sum(f.stat().st_size for f in app_dir.rglob("*") if f.is_file()) / 1024 / 1024
    print(f"已铺好 {app_dir}")
    print(f"  文件 {sum(1 for f in app_dir.rglob('*') if f.is_file())} 个 / {total_mb:.1f} MB（拷贝 {copied} 次）")

    if args.no_zip:
        return 0

    zip_path = out_dir / f"{args.name}.zip"
    if zip_path.exists():
        zip_path.unlink()

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for item in sorted(app_dir.rglob("*")):
            if item.is_file():
                zf.write(item, Path(args.name) / item.relative_to(app_dir))

    zip_mb = zip_path.stat().st_size / 1024 / 1024
    print(f"已打包 {zip_path}（{zip_mb:.1f} MB）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
