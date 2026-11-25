namespace Veil.Desktop;

internal enum SettingsSection { General, Privacy, Network, Accounts, Downloads, Advanced }
internal enum SettingsDestination { Security, SitePermissions, History, Bookmarks, Network, Passwords, Downloads, Vault, Search, SiteData, DefaultBrowser }

internal sealed record SettingsItem(SettingsSection Section, string Title, string Description, string Keywords,
    string? State = null, SettingsDestination? Destination = null)
{
    internal bool Matches(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => (Title + " " + Description + " " + Keywords + " " + State + " " +
                BrowserSettingsCatalog.SectionName(Section)).Contains(term, StringComparison.OrdinalIgnoreCase));
}

internal static class BrowserSettingsCatalog
{
    internal static string SectionName(SettingsSection section) => section switch
    {
        SettingsSection.General => "일반",
        SettingsSection.Privacy => "개인정보 및 보안",
        SettingsSection.Network => "네트워크",
        SettingsSection.Accounts => "계정 및 비밀번호",
        SettingsSection.Downloads => "다운로드",
        SettingsSection.Advanced => "고급 및 정보",
        _ => "",
    };

    internal static IReadOnlyList<SettingsItem> Build(BrowserSession session)
    {
        var chosen = session.Data.Network ?? NetworkSettings.Standard;
        var applied = session.Network;
        static string Mode(NetworkSettings settings) =>
            (settings.Mode == NetworkMode.Standard ? "표준 보호" : NetworkPolicy.ProxyName) +
            (settings.DnsTemplate is null ? " · 시스템 DNS" : " · 엔진 보안 DNS 설정");
        string networkState = "현재: " + Mode(applied);
        if (chosen != applied) networkState += "\n저장된 설정: " + Mode(chosen) + " (재시작 필요)";
        string proxyState = applied.Mode != NetworkMode.Proxy ? "프록시 모드 미사용" :
            session.ProxyReachable == true ? "시작 시 경로 확인됨" : "경로 미확인 또는 연결 실패";
        string smartScreen = (session.Security.Saved.SmartScreen ? "사용 설정" : "사용 안 함") + " · " +
            (session.Security.State switch
            {
                SmartScreenState.Applied => "엔진 설정 반영됨",
                SmartScreenState.Failed => "엔진 적용 실패",
                _ => "엔진 적용 대기",
            });
        var threat = session.Security.ThreatList;
        string threatState = !threat.Present ? "목록 없음" : threat.Valid ? "서명 검증 통과" : "목록 오류";
        var vault = session.Vault;
        string vaultState = !vault.Available ? "암호화 코어 없음" :
            session.VaultOpenGate.CurrentCount == 0 ? "작업 중" :
            vault.Unlocked ? "열림" : vault.Exists ? "잠김" : "미설정";
        var passwords = new PasswordStore(session.Root);
        string passwordState = !passwords.Available ? "암호화 코어 없음" :
            passwords.Exists ? "저장소 있음 · 열람 시 인증" : "미설정";
        return
        [
            new(SettingsSection.General, "검색 및 주소창", "기본 검색 엔진과 로컬 방문 기록·북마크 추천을 설정합니다.",
                "search address 검색 엔진 duckduckgo google bing naver 네이버 구글 자동완성",
                "기본 검색: " + SearchPolicy.Name(session.Data.SearchEngine) + " · 로컬 추천 " +
                    (session.Data.LocalAddressSuggestions != false ? "사용" : "사용 안 함"), SettingsDestination.Search),
            new(SettingsSection.General, "기본 브라우저", "Windows 기본 앱 설정에서 HTTP 및 HTTPS 링크의 기본 브라우저를 직접 선택합니다.",
                "default browser http https 기본 앱 외부 링크", ExternalAddress.Installed ? "설치본 · Windows 설정에서 선택" : "설치된 제품에서만 지원",
                SettingsDestination.DefaultBrowser),
            new(SettingsSection.General, "시작 및 세션", "이전 탭과 창, 사이트 데이터를 유지하는 현재 동작입니다.",
                "startup session 탭 복원 시작 페이지", "이전 세션 복원"),
            new(SettingsSection.General, "북마크", "저장된 페이지를 검색하거나 수정·삭제합니다.",
                "bookmarks 즐겨찾기", Destination: SettingsDestination.Bookmarks),
            new(SettingsSection.Privacy, "보안", "SmartScreen 설정과 VEIL 위협 목록 상태를 확인합니다.",
                "security smartscreen phishing 피싱 악성코드", smartScreen + "\nVEIL 위협 목록: " + threatState, SettingsDestination.Security),
            new(SettingsSection.Privacy, "사이트 권한", "카메라, 마이크, 위치, 알림을 사이트별로 관리합니다.",
                "permissions camera microphone location notifications 권한", "미설정 권한: 차단", SettingsDestination.SitePermissions),
            new(SettingsSection.Privacy, "쿠키 및 사이트 데이터", "쿠키 저장 도메인, 사이트 저장소 및 캐시를 관리합니다.",
                "cookies storage localstorage indexeddb cache 로그인 유지 데이터 삭제",
                "기본: 사이트 데이터 유지", SettingsDestination.SiteData),
            new(SettingsSection.Privacy, "방문 기록", "VEIL 방문 목록을 검색하고 선택한 항목 또는 기간을 삭제합니다.",
                "history browsing data 기록 삭제", Destination: SettingsDestination.History),
            new(SettingsSection.Privacy, "프로필 저장소", "프로필 및 사이트 데이터의 저장 위치 정책입니다. 검증 상태는 별도 출시 기준을 따릅니다.",
                "profile bitlocker 암호화 잠금 쿠키",
                vault.UsesProfilePassword ? "등록된 보호 프로필 사용" : "개발 프로필 · 제품 보호 미적용"),
            new(SettingsSection.Network, "네트워크 보호 및 보안 DNS", "표준 보호, 고정 프록시 및 엔진 DNS-over-HTTPS를 설정합니다. 금고의 별도 요청에 같은 DoH 설정이 적용되지는 않습니다.",
                "network proxy doh dns 프록시 https webrtc", networkState + "\n" + proxyState, SettingsDestination.Network),
            new(SettingsSection.Accounts, "비밀번호 관리자", "독립 암호로 보호되는 계정을 등록·관리하고 지원 로그인 폼에 채웁니다.",
                "password login autofill 로그인 자동완성 아이디 계정", passwordState, SettingsDestination.Passwords),
            new(SettingsSection.Downloads, "다운로드 목록", "현재 다운로드 결과와 보관된 항목을 확인합니다.",
                "downloads progress 파일 목록", Destination: SettingsDestination.Downloads),
            new(SettingsSection.Downloads, "다운로드 금고", "암호화된 파일을 관리하고 지원되는 내보내기·복구 기능을 이용합니다.",
                "vault export backup recovery 다운로드 백업 복구", vaultState, SettingsDestination.Vault),
            new(SettingsSection.Advanced, "브라우저 정보", "VEIL · Windows 데스크톱 · MPL-2.0",
                "about version license 버전 라이선스 정보", "버전: " +
                    (typeof(BrowserSettingsCatalog).Assembly.GetCustomAttributes(
                        typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "개발 빌드") +
                    " · 출시 보안 검증 미완료 · 관리자 오프라인 업데이트"),
            new(SettingsSection.Advanced, "브라우저 엔진", "Microsoft Edge WebView2 기반. 실제 설치된 런타임은 SDK 버전과 다를 수 있습니다.",
                "engine chromium webview2 runtime 엔진 런타임", session.EngineHost.DisplayName +
                    (session.EngineHost.RuntimeVersion is { } version ? " · " + version : " · 시작 전")),
        ];
    }
}
