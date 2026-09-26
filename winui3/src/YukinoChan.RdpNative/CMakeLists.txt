# YukinoChan.RdpNative — FreeRDP 桥接层（C ABI，计划书 §4）
#
# 构建（同 spike）：
#   cmake -S . -B build -G "Visual Studio 17 2022" -A x64
#   cmake --build build --config Release
# 产物：build/Release/ycn_rdp.dll（+ 运行时 DLL 全量拷贝）
#
# 依赖：vcpkg FreeRDP 3.32.0（默认 C:\Users\AITNR\WorkBuddy\_tools\vcpkg，纯 ASCII 路径）
#       可用 -DVCPKG_INST=<路径> 覆盖

cmake_minimum_required(VERSION 3.20)
project(YukinoChan.RdpNative C)

get_filename_component(VCPKG_INST
  "${CMAKE_CURRENT_SOURCE_DIR}/../../../../../_tools/vcpkg/installed/x64-windows"
  ABSOLUTE)

if(NOT EXISTS "${VCPKG_INST}/lib/freerdp3.lib")
  message(FATAL_ERROR "找不到 ${VCPKG_INST}/lib/freerdp3.lib —— 先在 _tools/vcpkg 里安装 freerdp[client]:x64-windows")
endif()

add_library(ycn_rdp SHARED ycn_rdp.c)
# 注意：不要定义 UNICODE/_UNICODE —— 会引发 freerdp rail.h 的 NIIF_* 与 shellapi.h 宏冲突
target_compile_definitions(ycn_rdp PRIVATE YCN_RDP_EXPORTS)

# 源码含 UTF-8 中文注释，MSVC 默认 CP936 会啃坏
target_compile_options(ycn_rdp PRIVATE /utf-8)

target_include_directories(ycn_rdp PRIVATE
  "${VCPKG_INST}/include"
  "${VCPKG_INST}/include/freerdp3"
  "${VCPKG_INST}/include/winpr3")

target_link_libraries(ycn_rdp PRIVATE
  "${VCPKG_INST}/lib/freerdp-client3.lib"
  "${VCPKG_INST}/lib/freerdp3.lib"
  "${VCPKG_INST}/lib/winpr3.lib"
  ws2_32.lib)

# 运行时 DLL 拷到输出目录（freerdp3/winpr3/openssl/zlib/cjson…）
add_custom_command(TARGET ycn_rdp POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E echo "Deploying FreeRDP runtime DLLs <- ${VCPKG_INST}/bin"
  COMMAND ${CMAKE_COMMAND} -E copy_directory "${VCPKG_INST}/bin" "$<TARGET_FILE_DIR:ycn_rdp>")
