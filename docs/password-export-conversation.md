# 비밀번호 가져오기·내보내기 대화 정리

> 이 문서는 이 대화에서 나온 요청과 확인한 내용을 정리한 기록이다. 실제 비밀번호나 Chrome 프로필 데이터는 읽지 않았다.

## 요청의 흐름

1. 사용자는 CodexDesk에서도 일반 Chrome처럼 아이디와 비밀번호를 자동 저장하고, Chrome에 이미 저장된 비밀번호를 가져오고 싶다고 했다.
2. 이어서 WebView2에 저장된 아이디와 비밀번호를 내보내는 기능이 있는지 물었다.
3. 최종적으로 Chrome처럼 CSV로 내보내 다른 브라우저에도 적용하고 싶다고 했다.
4. Chromium의 `PasswordManagerExporter`가 **어디에서 비밀번호를 가져오고 어떤 코드로 복호화하는지**를 질문했다. 중간에 답변이 CodexDesk 저장소를 설명하는 쪽으로 빗나갔고, 사용자가 Chromium 내부 코드를 묻는 것이라고 바로잡았다.

## 확인한 답변

### Chrome의 CSV 내보내기

Chrome에서는 **비밀번호 및 자동완성 → Google 비밀번호 관리자 → 설정 → 비밀번호 내보내기 → 파일 다운로드** 순서로 CSV를 만든다. Chromium의 CSV 작성 코드는 현재 `name,url,username,password,note` 열을 사용한다. 다른 브라우저는 이 파일을 읽어 자기 비밀번호 저장소에 추가한다. 예를 들어 Edge는 비밀번호 CSV 가져오기를 지원한다.

CSV 파일에는 비밀번호가 평문으로 기록되므로, 가져오기가 끝난 후 파일을 삭제해야 한다.

### Chromium 코드에서 비밀번호가 오는 경로

```text
Chrome 프로필 저장소: Login Data
Chrome 계정 저장소: Login Data For Account (사용하는 경우)
    ↓
PasswordStoreInterface
    ↓
SavedPasswordsPresenter::GetSavedCredentials()
    ↓
PasswordManagerExporter::PreparePasswordsForExport()
    ↓
PasswordCSVWriter::SerializePasswords()
    ↓
CSV 파일
```

Windows용 Chrome의 기본 프로필이라면 `Login Data` 파일의 일반적인 경로는 다음과 같다.

```text
%LOCALAPPDATA%\Google\Chrome\User Data\Default\Login Data
```

다른 프로필은 `Default` 대신 `Profile 1` 등의 폴더를 사용한다. 계정 저장소의 파일명은 `Login Data For Account`이다. 실행 중인 Chrome의 정확한 프로필 폴더는 `chrome://version`의 **프로필 경로**에서 확인할 수 있다. 설치 파일 폴더에 저장되는 것이 아니다.

### 복호화 코드

Chromium 소스에서 `LoginDatabase`는 SQLite `logins` 테이블의 `password_value`를 암호문으로 읽는다. `LoginDatabase::DecryptStringToPasswordString()`은 `encryptor_->DecryptString()`을 호출한다. 실제 암호 처리는 `OSCryptAsync`의 `Encryptor`와 운영체제별 키 공급자에 이어진다. 따라서 저장 파일 위치와 복호화 코드 모두 공개되어 있지만, `Login Data` 파일만 CSV처럼 읽어서 비밀번호를 얻는 구조는 아니다.

### CodexDesk와의 관계

현재 CodexDesk는 WebView2의 비밀번호 자동 저장 설정만 사용한다. WebView2 프로필은 `%LOCALAPPDATA%\CodexDesk\WebView2`에 있으며 Chrome 프로필과 별개다. 현재 앱에는 Chrome CSV 가져오기, 앱 자체 비밀번호 저장소, 비밀번호 CSV 내보내기가 없다. WebView2 공개 프로필 API에는 자동 저장 설정이 있지만 저장된 비밀번호 목록을 읽거나 추가하는 메서드는 보이지 않는다. 따라서 Chrome CSV와 양방향 호환이 필요하면 앱 자체 저장소와 가져오기·자동 입력·내보내기 기능을 설계해야 한다.

## 확인한 자료

- [Google Chrome: 비밀번호 가져오기·내보내기](https://support.google.com/chrome/answer/13068232?hl=en)
- [Microsoft Edge: 비밀번호 CSV 가져오기](https://support.microsoft.com/en-us/edge/import-your-favorites-and-passwords-in-microsoft-edge)
- [Chromium: PasswordManagerExporter](https://chromium.googlesource.com/chromium/src/+/HEAD/components/password_manager/core/browser/export/password_manager_exporter.cc)
- [Chromium: PasswordCSVWriter](https://github.com/chromium/chromium/blob/main/components/password_manager/core/browser/export/password_csv_writer.cc)
- [Chromium: SavedPasswordsPresenter](https://github.com/chromium/chromium/blob/main/components/password_manager/core/browser/ui/saved_passwords_presenter.cc)
- [Chromium: 저장소 파일 경로 구성](https://chromium.googlesource.com/chromium/src/+/HEAD/components/password_manager/core/browser/password_store_factory_util.cc)
- [Chromium: 저장소 파일명](https://chromium.googlesource.com/chromium/src/+/HEAD/components/password_manager/core/browser/password_manager_constants.cc)
- [Chromium: LoginDatabase 복호화 호출](https://chromium.googlesource.com/chromium/src/+/HEAD/components/password_manager/core/browser/password_store/login_database.cc)
- [Chromium: OSCryptAsync 구조](https://chromium.googlesource.com/chromium/src/+/HEAD/components/os_crypt/async/README.md)
- [Chromium: Chrome 프로필 경로](https://chromium.googlesource.com/chromium/src/+/HEAD/docs/user_data_dir.md)
- [Microsoft: WebView2 프로필 API](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2profile)
