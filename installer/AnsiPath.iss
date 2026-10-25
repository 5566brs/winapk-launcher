; AnsiPath.iss -- the install folder's check against the ANSI code page, for WinApkLauncher.iss
; ([Code], included there).
;
; The runtime's JVM reads paths in the ANSI code page ("Language for non-Unicode programs"). The
; runtime asks for UTF-8 in its manifest, which Windows 10 1903 and later honors; before that, a
; user name in a script the code page cannot hold (Hebrew on an English system, say) breaks every
; app. So when {localappdata} has such a character, the default install folder is
; C:\WinApk Launcher on the system drive: still per user, no administrator rights (Setup creates
; the folder; users may create folders at the root of the system drive).

[Code]
const
  CP_ACP = 0;
  WC_NO_BEST_FIT_CHARS = $400;

function WideCharToMultiByte(CodePage, dwFlags: Cardinal; lpWideCharStr: String; cchWideChar: Integer;
  lpMultiByteStr: AnsiString; cbMultiByte: Integer; lpDefaultChar, lpUsedDefaultChar: Cardinal): Integer;
  external 'WideCharToMultiByte@kernel32.dll stdcall';
function MultiByteToWideChar(CodePage, dwFlags: Cardinal; lpMultiByteStr: AnsiString; cbMultiByte: Integer;
  lpWideCharStr: String; cchWideChar: Integer): Integer;
  external 'MultiByteToWideChar@kernel32.dll stdcall';
function GetACP: Cardinal;
  external 'GetACP@kernel32.dll stdcall';

// True when every character of S survives a round trip through the code page: WideCharToMultiByte
// without best-fit mappings, then MultiByteToWideChar, and the result equals S.
function SurvivesCodePage(const S: String; CodePage: Cardinal): Boolean;
var
  A: AnsiString;
  W: String;
  N, M: Integer;
begin
  Result := True;
  if (S = '') or (CodePage = 65001) then Exit;
  N := WideCharToMultiByte(CodePage, WC_NO_BEST_FIT_CHARS, S, Length(S), A, 0, 0, 0);
  if N <= 0 then begin Result := False; Exit; end;
  SetLength(A, N);
  N := WideCharToMultiByte(CodePage, WC_NO_BEST_FIT_CHARS, S, Length(S), A, N, 0, 0);
  if N <= 0 then begin Result := False; Exit; end;
  M := MultiByteToWideChar(CodePage, 0, A, N, W, 0);
  if M <= 0 then begin Result := False; Exit; end;
  SetLength(W, M);
  M := MultiByteToWideChar(CodePage, 0, A, N, W, M);
  Result := (M = Length(S)) and (W = S);
end;

function SurvivesAnsiCodePage(const S: String): Boolean;
begin
  Result := SurvivesCodePage(S, CP_ACP);
end;

// The install folder for a %LOCALAPPDATA% and a system drive: LocalAppData\Programs\WinApk Launcher,
// or <drive>\WinApk Launcher when LocalAppData has a character the ANSI code page cannot hold.
function InstallDirFor(const LocalAppData, SystemDrive: String): String;
begin
  if SurvivesAnsiCodePage(LocalAppData) then
    Result := AddBackslash(LocalAppData) + 'Programs\WinApk Launcher'
  else
    Result := AddBackslash(SystemDrive) + 'WinApk Launcher';
end;

// DefaultDirName: see InstallDirFor.
function DefaultInstallDir(Param: String): String;
begin
  Result := InstallDirFor(ExpandConstant('{localappdata}'), ExpandConstant('{sd}'));
  if not SurvivesAnsiCodePage(ExpandConstant('{localappdata}')) then
    Log('The ANSI code page (' + IntToStr(GetACP) + ') cannot hold ' + ExpandConstant('{localappdata}') +
        '; installing to ' + Result + '.');
end;
