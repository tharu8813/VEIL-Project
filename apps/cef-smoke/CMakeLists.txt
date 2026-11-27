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

# Standalone native protocol test; never launches CEF or contacts a real broker.
add_executable(veil-cef-broker-wire-test broker_verification_test.cc broker_verification.cc)
target_compile_features(veil-cef-broker-wire-test PRIVATE cxx_std_17)
target_link_libraries(veil-cef-broker-wire-test PRIVATE advapi32)

add_executable(veil-cef-protected-path-test protected_paths_test.cc protected_bootstrap.cc broker_verification.cc)
target_compile_features(veil-cef-protected-path-test PRIVATE cxx_std_17)
target_link_libraries(veil-cef-protected-path-test PRIVATE advapi32)

add_executable(veil-cef-registration-test bootstrap_registration_test.cc bootstrap_registration.cc)
target_compile_features(veil-cef-registration-test PRIVATE cxx_std_17)
target_link_libraries(veil-cef-registration-test PRIVATE advapi32)

add_library(veil-cef-smoke SHARED smoke.cc)
add_dependencies(veil-cef-smoke veil-cef-registration-test)
add_dependencies(veil-cef-smoke veil-cef-protected-path-test)
add_dependencies(veil-cef-smoke veil-cef-broker-wire-test)
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

if(DOTNET_HOST_PACK)
  add_library(veil-cef-wpf SHARED wpf.cc)
  SET_LIBRARY_TARGET_PROPERTIES(veil-cef-wpf)
  target_include_directories(veil-cef-wpf PRIVATE "${CEF_ROOT}" "${DOTNET_HOST_PACK}")
  target_compile_definitions(veil-cef-wpf PRIVATE NETHOST_USE_AS_STATIC)
  target_link_libraries(veil-cef-wpf PRIVATE libcef_dll_wrapper "${CEF_LIB_RELEASE}"
    "${DOTNET_HOST_PACK}/libnethost.lib" ${CEF_STANDARD_LIBS} ole32 user32)
  add_dependencies(veil-cef-wpf veil-cef-smoke)
  add_custom_command(TARGET veil-cef-wpf POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      "${CEF_BINARY_DIR_RELEASE}/bootstrap.exe" "$<TARGET_FILE_DIR:veil-cef-wpf>/veil-cef-wpf.exe")
endif()


# Product-named compile candidate only. Isolated from all smoke/managed
# fixture output; it intentionally cannot start a browser.
add_executable(veil-cef-product-preflight-test product_preflight_test.cc)
target_compile_features(veil-cef-product-preflight-test PRIVATE cxx_std_17)

add_library(veil-cef-product SHARED EXCLUDE_FROM_ALL product_candidate.cc
  protected_bootstrap.cc bootstrap_registration.cc broker_verification.cc)
target_compile_features(veil-cef-product PRIVATE cxx_std_17)
add_dependencies(veil-cef-product veil-cef-product-preflight-test)
SET_LIBRARY_TARGET_PROPERTIES(veil-cef-product)
set_target_properties(veil-cef-product PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/product-candidate"
  LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/product-candidate"
  ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/product-import"
  PDB_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/product-symbols")
target_include_directories(veil-cef-product PRIVATE "${CEF_ROOT}")
target_link_libraries(veil-cef-product PRIVATE libcef_dll_wrapper "${CEF_LIB_RELEASE}"
  ${CEF_STANDARD_LIBS} user32 advapi32)
set(product_candidate_dir "$<TARGET_FILE_DIR:veil-cef-product>")
COPY_FILES(veil-cef-product "${CEF_BINARY_FILES}" "${CEF_BINARY_DIR_RELEASE}" "${product_candidate_dir}")
COPY_FILES(veil-cef-product "${CEF_RESOURCE_FILES}" "${CEF_RESOURCE_DIR}" "${product_candidate_dir}")
add_custom_command(TARGET veil-cef-product POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${CEF_BINARY_DIR_RELEASE}/bootstrap.exe" "${product_candidate_dir}/veil-cef-product.exe"
  COMMAND ${CMAKE_COMMAND} -E rm -f "${product_candidate_dir}/bootstrap.exe")
