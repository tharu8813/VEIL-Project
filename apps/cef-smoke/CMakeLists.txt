cmake_minimum_required(VERSION 3.21)
project(veil_cef_smoke LANGUAGES C CXX)

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "The CEF smoke host requires Windows x64.")
endif()

set(PROJECT_ARCH x86_64)
file(TO_CMAKE_PATH "${CEF_ROOT}" CEF_ROOT)
list(PREPEND CMAKE_MODULE_PATH "${CEF_ROOT}/cmake")
find_package(CEF REQUIRED)
add_subdirectory("${CEF_LIBCEF_DLL_WRAPPER_PATH}" libcef_dll_wrapper)

add_library(veil-cef-smoke SHARED smoke.cc)
SET_LIBRARY_TARGET_PROPERTIES(veil-cef-smoke)
target_include_directories(veil-cef-smoke PRIVATE "${CEF_ROOT}")
target_link_libraries(veil-cef-smoke PRIVATE libcef_dll_wrapper "${CEF_LIB_RELEASE}"
  ${CEF_STANDARD_LIBS} user32)

set(runtime_dir "$<TARGET_FILE_DIR:veil-cef-smoke>")
COPY_FILES(veil-cef-smoke "${CEF_BINARY_FILES}" "${CEF_BINARY_DIR_RELEASE}" "${runtime_dir}")
COPY_FILES(veil-cef-smoke "${CEF_RESOURCE_FILES}" "${CEF_RESOURCE_DIR}" "${runtime_dir}")
add_custom_command(TARGET veil-cef-smoke POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${CEF_BINARY_DIR_RELEASE}/bootstrap.exe" "${runtime_dir}/veil-cef-smoke.exe")
