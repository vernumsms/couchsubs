# SPDX-License-Identifier: AGPL-3.0-only
# couchsubs_native.dll: libchiaki + FFmpeg (h264 çözücü, swscale) tek DLL'de, statik bağlı.
# Windows'ta MSYS2 mingw64 ile derlenir (.github/workflows/windows.yml); Mac'te de derlenir
# (kod ve testler için; orada paylaşımlı kütüphane .dylib olur).
#
#   cmake -S windows/native -B build/native -G Ninja -DFFMPEG_PREFIX=<statik FFmpeg kurulumu>
#   cmake --build build/native
cmake_minimum_required(VERSION 3.20)
project(couchsubs_native C)

set(CHIAKI_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../third-party/chiaki-ng" CACHE PATH "chiaki-ng kaynakları")
set(FFMPEG_PREFIX "" CACHE PATH "FFmpeg kurulumu (include/, lib/)")
option(COUCHSUBS_NATIVE_TESTS "Yerel testleri derle" ON)

if(WIN32)
	# Yalnız statik kütüphaneler bulunsun: DLL, MSYS2'nin çalışma zamanı DLL'lerine bağlı kalmamalı.
	set(CMAKE_FIND_LIBRARY_SUFFIXES .a)
	set(OPENSSL_USE_STATIC_LIBS ON CACHE BOOL "" FORCE)
	# miniupnpc başlıkları aksi halde __declspec(dllimport) bildirir.
	add_compile_definitions(MINIUPNP_STATICLIB)
endif()

# Mac betiğiyle (scripts/build-chiaki-macos.sh) aynı seçenekler: yalnız bağlantı, kayıt ve görüntü akışı.
foreach(off CHIAKI_ENABLE_GUI CHIAKI_ENABLE_CLI CHIAKI_ENABLE_TESTS CHIAKI_ENABLE_FFMPEG_DECODER
		CHIAKI_ENABLE_PI_DECODER CHIAKI_LIB_ENABLE_OPUS CHIAKI_ENABLE_SPEEX CHIAKI_ENABLE_SETSU
		CHIAKI_ENABLE_STEAMDECK_NATIVE CHIAKI_ENABLE_STEAM_SHORTCUT CHIAKI_ENABLE_BOREALIS
		BUILD_SHARED_LIBS USE_NGHTTP2 USE_LIBIDN2 CURL_ZSTD CURL_BROTLI CURL_USE_LIBPSL CURL_USE_LIBSSH2
		USE_LIBRTMP CURL_ZLIB)
	set(${off} OFF CACHE STRING "" FORCE)
endforeach()
set(CURL_DISABLE_LDAP ON CACHE BOOL "" FORCE)
add_subdirectory("${CHIAKI_SOURCE_DIR}" chiaki EXCLUDE_FROM_ALL)

# FFmpeg: Windows'ta kaynaktan derlenmiş statik kütüphaneler (LGPL; yalnız h264 + swscale).
foreach(lib avcodec swresample swscale avutil)
	find_library(FFMPEG_${lib} NAMES ${lib} HINTS "${FFMPEG_PREFIX}/lib" NO_DEFAULT_PATH)
	if(NOT FFMPEG_${lib})
		find_library(FFMPEG_${lib} NAMES ${lib} REQUIRED)
	endif()
endforeach()
find_path(FFMPEG_INCLUDE_DIR libavcodec/avcodec.h HINTS "${FFMPEG_PREFIX}/include")

add_library(couchsubs_deps INTERFACE)
target_link_libraries(couchsubs_deps INTERFACE chiaki-lib ${FFMPEG_avcodec} ${FFMPEG_swresample} ${FFMPEG_swscale} ${FFMPEG_avutil})
target_include_directories(couchsubs_deps INTERFACE "${FFMPEG_INCLUDE_DIR}" "${CMAKE_CURRENT_SOURCE_DIR}")
if(WIN32)
	# Statik OpenSSL, libevent, curl (Schannel) ve FFmpeg'in Windows bağımlılıkları; hepsi sistem DLL'i.
	target_link_libraries(couchsubs_deps INTERFACE
		ws2_32 wsock32 iphlpapi bcrypt crypt32 secur32 advapi32 user32 shell32 ole32 gdi32 normaliz)
	target_link_options(couchsubs_deps INTERFACE -static -static-libgcc)
elseif(APPLE)
	target_link_libraries(couchsubs_deps INTERFACE z "-framework CoreFoundation" "-framework Security"
		"-framework SystemConfiguration" "-framework CoreServices")
endif()

add_library(couchsubs_native SHARED couchsubs_native.c couchsubs_native.h)
target_link_libraries(couchsubs_native PRIVATE couchsubs_deps)
set_target_properties(couchsubs_native PROPERTIES PREFIX "" C_VISIBILITY_PRESET hidden)
if(WIN32)
	# Hata ayıklama sembolleri atılır (DLL ~10 MB'tan küçülür).
	target_link_options(couchsubs_native PRIVATE -Wl,--no-undefined -s)
endif()

if(COUCHSUBS_NATIVE_TESTS)
	# Test, iç işlevlere erişmek için kaynak dosyayı doğrudan içerir.
	add_executable(couchsubs_native_test tests/native_test.c)
	target_link_libraries(couchsubs_native_test PRIVATE couchsubs_deps)
endif()
