; ═══════════════════════════════════════════════════════════════════════════════
; 雪乃酱 · 安装程序（Inno Setup 6）
;
; 被 CI 调用（build.yml）：ISCC 传 MyAppVersion / SrcDir / OutDir / IconFile。
;   ISCC.exe winui3\tools\installer.iss /DMyAppVersion=2.1.0 /DSrcDir=<abs>\dist\app /DOutDir=<abs>\dist
; ⚠️ SrcDir / OutDir 请传**绝对路径** —— 相对路径会被当成「相对本脚本所在目录」解析。
;
; 默认装到公共目录 {pf}\YukinoChan（Program Files）。程序启动时会自己提权重启
; （Program.cs 的 TryRelaunchElevated），所以往安装目录写 config.json / logs 没问题。
;
; 数据（config.json / logs / runtime_stats）不在 [Files] 里，因此：
;   · 升级安装 = 覆盖程序文件、保留配置；
;   · 卸载后配置和日志也会留在安装目录，需要清掉自己删。
; ═══════════════════════════════════════════════════════════════════════════════

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#ifndef SrcDir
  #define SrcDir "dist\app"
#endif

#ifndef OutDir
  #define OutDir "dist"
#endif

; 留空 = 不设安装程序图标（默认图标）
#ifndef IconFile
  #define IconFile ""
#endif

; 留空 = 不加载语言文件（向导用默认英文）。
; Inno Setup 6.7 的 choco 包不带 Languages 目录，中文 isl 得另外下载 ——
; CI 里下载到了才传这个参数，下载不到就退回英文，别让这一步把构建搞红。
#ifndef LangFile
  #define LangFile ""
#endif

#define MyAppName "雪乃酱"
#define MyAppExe "YukinoChan.exe"
#define MyAppPublisher "AITNR"
#define MyAppURL "https://github.com/AITNR/Anigame-script-manager"

[Setup]
AppId={{D7A2C45E-9F3B-4A61-8C2D-1E5B7F0A93C6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
; ⚠️ 别写 UpdatesInfoURL / AppUpdatesInfoURL —— 这指令在新版 Inno 才有，
;    runner 上装的版本（6.2.x）不认，编译直接 abort。

; 公共目录（Program Files）—— 装这里必须管理员，下面显式要求
DefaultDirName={pf}\YukinoChan
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; 装到 Program Files 就要管理员：不降权装到用户目录（那样数据与程序就分家了）
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputBaseFilename=YukinoChan-{#MyAppVersion}-setup
OutputDir={#OutDir}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExe}
SetupLogging=yes

; ⚠️ 别写 VersionInfoVersion：CI 传的版本可能是 2.0.0-dev.42 这种带后缀的，
;    Inno 只接受纯点分数字，写了会编译失败。

#if IconFile != ""
SetupIconFile={#IconFile}
#endif

#if LangFile != ""
[Languages]
Name: "chinesesimplified"; MessagesFile: "{#LangFile}"
#endif

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "安装完成后启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
