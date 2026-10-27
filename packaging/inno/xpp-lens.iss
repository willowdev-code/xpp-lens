; xpp-lens setup program: per user, no administrator rights, a wizard with windows (Polish or English).
; Built by pack.ps1:  ISCC /DVersion=X.Y.Z /DSource=<staged package> /DOutDir=<dist> xpp-lens.iss
; The wizard calls xpplens.exe for the real work (detect, config, register, build), so every step stays scriptable.
; Silent update (xpplens update --install):  xpp-lens-setup-X.Y.Z.exe /SILENT /SUPPRESSMSGBOXES /NORESTART /DIR="..."
;   [/RESTARTCLAUDE=1]  restarts Claude Desktop at the end.

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Source
  #define Source "..\..\dist\xpp-lens"
#endif
#ifndef OutDir
  #define OutDir "..\..\dist"
#endif
; The file version is numbers only: 1.3.0 for a test release 1.3.0-beta.2.
#define FileVersion Copy(Version, 1, Pos("-", Version + "-") - 1)

[Setup]
AppId={{B3C9E031-73CD-4356-AC30-B8EC91C3C528}
AppName=xpp-lens
AppVersion={#Version}
AppVerName=xpp-lens {#Version}
AppPublisher=WillowDev
AppPublisherURL=https://github.com/willowdev-code/xpp-lens
AppSupportURL=https://github.com/willowdev-code/xpp-lens/issues
; An installation made by install.ps1 (C:\Tools\xpp-lens) is taken over in place: settings and index stay.
DefaultDirName={code:DefaultInstallDir}
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=xpp-lens
DisableReadyPage=yes
SetupIconFile={#SourcePath}..\icon\xpplens.ico
; The program folder goes on the user's PATH, so "xpplens" works in any new terminal.
ChangesEnvironment=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutDir}
OutputBaseFilename=xpp-lens-setup-{#Version}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=xpp-lens
UninstallDisplayIcon={app}\bin\xpplens.exe
LicenseFile={#Source}\LICENSE
ShowLanguageDialog=yes
CloseApplications=no
VersionInfoVersion={#FileVersion}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"

[InstallDelete]
; The package's copy of the skills is replaced whole, so a skill dropped from the package does not linger.
Type: filesandordirs; Name: "{app}\skills"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
; Start menu: commands open in a console that stays open (xpplens commands work there too).
; "xpp-lens" is the general one: the command list, then any xpplens command (PATH set here too,
; in case Explorer has not picked up the new PATH yet).
Name: "{group}\xpp-lens"; Filename: "{cmd}"; Parameters: "/k ""title xpp-lens & set ""PATH={app}\bin;%PATH%"" & xpplens help"""; WorkingDir: "{%USERPROFILE}"; IconFilename: "{app}\bin\xpplens.exe"; Comment: "{cm:IconConsole}"
Name: "{group}\{cm:IconStatus}"; Filename: "{cmd}"; Parameters: "/k """"{app}\bin\xpplens.exe"" status"""; WorkingDir: "{app}"; IconFilename: "{app}\bin\xpplens.exe"
Name: "{group}\{cm:IconBuild}"; Filename: "{cmd}"; Parameters: "/k """"{app}\bin\xpplens.exe"" build"""; WorkingDir: "{app}"; IconFilename: "{app}\bin\xpplens.exe"
Name: "{group}\{cm:IconUpdate}"; Filename: "{cmd}"; Parameters: "/k """"{app}\bin\xpplens.exe"" update"""; WorkingDir: "{app}"; IconFilename: "{app}\bin\xpplens.exe"
Name: "{group}\{cm:UninstallProgram,xpp-lens}"; Filename: "{uninstallexe}"; IconFilename: "{app}\bin\xpplens.exe"

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\bin"; Check: NeedsAddPath(ExpandConstant('{app}\bin'))

[UninstallDelete]
Type: files; Name: "{app}\bin\*.old"

[UninstallRun]
Filename: "{app}\bin\xpplens.exe"; Parameters: "unregister"; Flags: runhidden waituntilterminated; RunOnceId: "unregister"

[Run]
Filename: "{cmd}"; Parameters: "/k """"{app}\bin\xpplens.exe"" build"""; WorkingDir: "{app}"; Description: "{cm:BuildNow}"; Flags: postinstall nowait skipifsilent; Check: IsFreshInstall
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\restart-claude.ps1"" -Version {#Version}"; Description: "{cm:RestartClaude}"; Flags: postinstall nowait skipifsilent unchecked runhidden; Check: IsUpdate
Filename: "{cm:ReadmeUrl}"; Description: "{cm:OpenReadme}"; Flags: postinstall shellexec skipifsilent unchecked

[CustomMessages]
en.PkgTitle=PackagesLocalDirectory
en.PkgSub=Where the X++ code of this machine is
en.PkgText=xpp-lens indexes the folder with the AOT packages (read only - nothing is ever written there).
en.PkgLabel=Folder:
en.PkgDetecting=Looking for PackagesLocalDirectory...
en.PkgFound=Found:%n%1
en.PkgNone=No PackagesLocalDirectory was found automatically - choose the folder (usually ...\AosService\PackagesLocalDirectory).
en.PkgInvalid=This folder does not look like a PackagesLocalDirectory (no packages with a Descriptor folder):%n%n%1
en.LangTitle=Label languages
en.LangSub=Label texts shown next to label ids
en.LangText=Languages of the label texts to index, separated by commas (for example en-US, pl, de). More languages make the index larger.
en.LangLabel=Languages:
en.StdTitle=Microsoft standard code
en.StdSub=Objects, signatures and calls of the standard models
en.StdText=Recommended: Claude then also finds callers and extensions inside Microsoft code. The first indexing takes 20-90 minutes (in the background), later only what changed.
en.StdOption=Index the Microsoft standard models (recommended)
en.SkillsTitle=Claude Code skills
en.SkillsSub=xpp-ponytail and xpp-ponytail-review
en.SkillsText=Two skills for X++ work in Claude Code: /xpp-ponytail (the smallest complete change: configuration, standard, extension, only then new code) and /xpp-ponytail-review (a review of an X++ change). They go to .claude\skills in your user profile; a skill you change there is never overwritten.
en.SkillsOption=Install the Claude Code skills (xpp-ponytail)
en.SkillsKept=Skills you changed were kept and not updated:%n%n%1
en.ConfigFailed=Saving the settings did not work:%n%n%1
en.Registered=xpp-lens is ready. Open Claude and ask, for example: "find the method SalesTable.validateWrite".
en.BuildNow=Build the index now (a console window shows the progress)
en.RestartClaude=Restart Claude Desktop so all its sessions use the new version (interrupts work in progress there)
en.OpenReadme=Read how to use xpp-lens
en.ReadmeUrl=https://github.com/willowdev-code/xpp-lens#readme
en.IconConsole=Console with the xpplens commands
en.IconStatus=xpp-lens - status
en.IconBuild=xpp-lens - update the index
en.IconUpdate=xpp-lens - check for updates
en.RemoveData=Also remove the index (about 1 GB), the settings and the usage log of xpp-lens?%n%nChoose No to keep them for a later installation.
pl.PkgTitle=PackagesLocalDirectory
pl.PkgSub=Gdzie jest kod X++ tej maszyny
pl.PkgText=xpp-lens indeksuje folder z pakietami AOT (tylko odczyt - nic tam nie jest zapisywane).
pl.PkgLabel=Folder:
pl.PkgDetecting=Szukam PackagesLocalDirectory...
pl.PkgFound=Znaleziono:%n%1
pl.PkgNone=Nie znaleziono PackagesLocalDirectory automatycznie - wskaż folder (zwykle ...\AosService\PackagesLocalDirectory).
pl.PkgInvalid=Ten folder nie wygląda na PackagesLocalDirectory (brak pakietów z folderem Descriptor):%n%n%1
pl.LangTitle=Języki etykiet
pl.LangSub=Teksty etykiet pokazywane obok ich identyfikatorów
pl.LangText=Języki tekstów etykiet do indeksu, po przecinku (np. en-US, pl, de). Więcej języków to większy indeks.
pl.LangLabel=Języki:
pl.StdTitle=Kod standardowy Microsoftu
pl.StdSub=Obiekty, sygnatury i wywołania modeli standardowych
pl.StdText=Zalecane: Claude znajdzie wtedy także wywołania i rozszerzenia w kodzie Microsoftu. Pierwsze indeksowanie trwa 20-90 minut (w tle), później tylko zmiany.
pl.StdOption=Indeksuj modele standardowe Microsoftu (zalecane)
pl.SkillsTitle=Skille Claude Code
pl.SkillsSub=xpp-ponytail i xpp-ponytail-review
pl.SkillsText=Dwa skille do pracy z X++ w Claude Code: /xpp-ponytail (najmniejsza kompletna zmiana: konfiguracja, standard, rozszerzenie, dopiero potem nowy kod) i /xpp-ponytail-review (przegląd zmiany X++). Trafiają do .claude\skills w profilu użytkownika; skilla, którego tam zmienisz, instalator nigdy nie nadpisze.
pl.SkillsOption=Zainstaluj skille Claude Code (xpp-ponytail)
pl.SkillsKept=Zmienione przez ciebie skille zostały zachowane i nie zostały zaktualizowane:%n%n%1
pl.ConfigFailed=Nie udało się zapisać ustawień:%n%n%1
pl.Registered=xpp-lens jest gotowy. Otwórz Claude'a i zapytaj np.: "znajdź metodę SalesTable.validateWrite".
pl.BuildNow=Zbuduj indeks teraz (okno konsoli pokaże postęp)
pl.RestartClaude=Uruchom ponownie Claude Desktop, żeby wszystkie jego sesje używały nowej wersji (przerywa trwającą tam pracę)
pl.OpenReadme=Przeczytaj, jak używać xpp-lens
pl.ReadmeUrl=https://github.com/willowdev-code/xpp-lens/blob/main/README.pl.md
pl.IconConsole=Konsola z poleceniami xpplens
pl.IconStatus=xpp-lens - status
pl.IconBuild=xpp-lens - aktualizacja indeksu
pl.IconUpdate=xpp-lens - sprawdź aktualizacje
pl.RemoveData=Usunąć też indeks (ok. 1 GB), ustawienia i dziennik użycia xpp-lens?%n%nWybierz Nie, aby zachować je na potrzeby ponownej instalacji.

[Code]
var
  PkgPage: TInputDirWizardPage;
  PkgInfo: TNewStaticText;
  LangPage: TInputQueryWizardPage;
  StdPage: TInputOptionWizardPage;
  SkillsPage: TInputOptionWizardPage;
  Detected: Boolean;
  WasUpdate: Boolean;
  RemoveUserData: Boolean;

{ Where to install: an installation made by install.ps1 is taken over in place, else the per-user programs folder. }
function DefaultInstallDir(Param: String): String;
begin
  if FileExists('C:\Tools\xpp-lens\bin\xpplens.exe') then
    Result := 'C:\Tools\xpp-lens'
  else
    Result := ExpandConstant('{localappdata}\Programs\xpp-lens');
end;

{ An installation with settings (an update) skips the questions and keeps its settings and index. }
function HasSettings: Boolean;
begin
  Result := FileExists(AddBackslash(WizardDirValue) + 'xpplens.json');
end;

function IsFreshInstall: Boolean;
begin
  Result := not WasUpdate;
end;

function IsUpdate: Boolean;
begin
  Result := WasUpdate;
end;

{ Whether a folder is missing from the user's PATH (compared without case and trailing backslash). }
function NeedsAddPath(Dir: String): Boolean;
var
  Path: String;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
  begin
    Result := True;
    Exit;
  end;
  Result := Pos(';' + Lowercase(RemoveBackslashUnlessRoot(Dir)) + ';', ';' + Lowercase(Path) + ';') = 0;
end;

procedure RemoveFromPath(Dir: String);
var
  Path, Item, Kept: String;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then Exit;
  Kept := '';
  Path := Path + ';';
  while Path <> '' do
  begin
    P := Pos(';', Path);
    Item := Copy(Path, 1, P - 1);
    Delete(Path, 1, P);
    if (Item <> '') and (Lowercase(RemoveBackslashUnlessRoot(Item)) <> Lowercase(RemoveBackslashUnlessRoot(Dir))) then
    begin
      if Kept <> '' then Kept := Kept + ';';
      Kept := Kept + Item;
    end;
  end;
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Kept);
end;

{ Runs xpplens.exe with its output in a file; returns the exit code and the output. }
function RunXpp(const Exe, Params: String; var Output: String): Integer;
var
  OutFile: String;
  Text: AnsiString;
  Code: Integer;
begin
  OutFile := ExpandConstant('{tmp}\xpplens-out.txt');
  DeleteFile(OutFile);
  if not Exec(ExpandConstant('{cmd}'), '/C ""' + Exe + '" ' + Params + ' > "' + OutFile + '" 2>&1"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Output := '';
  if LoadStringFromFile(OutFile, Text) then Output := Trim(String(Text));
  Result := Code;
end;

{ Number of packages (subfolders with a Descriptor folder) - a PackagesLocalDirectory has many. }
function CountPackages(Dir: String): Integer;
var
  F: TFindRec;
begin
  Result := 0;
  if not FindFirst(AddBackslash(Dir) + '*', F) then Exit;
  try
    repeat
      if (F.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and (F.Name <> '.') and (F.Name <> '..') and
         DirExists(AddBackslash(Dir) + F.Name + '\Descriptor') then
        Result := Result + 1;
    until (Result >= 3) or not FindNext(F);
  finally
    FindClose(F);
  end;
end;

procedure InitializeWizard;
begin
  PkgPage := CreateInputDirPage(wpLicense, CustomMessage('PkgTitle'), CustomMessage('PkgSub'), CustomMessage('PkgText'), False, '');
  PkgPage.Add(CustomMessage('PkgLabel'));
  PkgInfo := TNewStaticText.Create(PkgPage);
  PkgInfo.Parent := PkgPage.Surface;
  PkgInfo.AutoSize := False;
  PkgInfo.WordWrap := True;
  PkgInfo.Width := PkgPage.SurfaceWidth;
  PkgInfo.Height := ScaleY(80);
  PkgInfo.Top := PkgPage.Edits[0].Top + PkgPage.Edits[0].Height + ScaleY(16);
  PkgInfo.Caption := CustomMessage('PkgDetecting');

  LangPage := CreateInputQueryPage(PkgPage.ID, CustomMessage('LangTitle'), CustomMessage('LangSub'), CustomMessage('LangText'));
  LangPage.Add(CustomMessage('LangLabel'), False);
  if ActiveLanguage = 'pl' then LangPage.Values[0] := 'en-US, pl' else LangPage.Values[0] := 'en-US';

  StdPage := CreateInputOptionPage(LangPage.ID, CustomMessage('StdTitle'), CustomMessage('StdSub'), CustomMessage('StdText'), False, False);
  StdPage.Add(CustomMessage('StdOption'));
  StdPage.Values[0] := True;

  { Shown on updates too; a silent update (xpplens update --install) keeps the choice of the last installation. }
  SkillsPage := CreateInputOptionPage(StdPage.ID, CustomMessage('SkillsTitle'), CustomMessage('SkillsSub'), CustomMessage('SkillsText'), False, False);
  SkillsPage.Add(CustomMessage('SkillsOption'));
  SkillsPage.Values[0] := GetPreviousData('Skills', '1') = '1';
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  if SkillsPage.Values[0] then
    SetPreviousData(PreviousDataKey, 'Skills', '1')
  else
    SetPreviousData(PreviousDataKey, 'Skills', '0');
end;

{ The "warning:" lines of "xpplens skills install / remove" (skills the user changed, kept as they are). }
function SkillWarnings(Output: String): String;
var
  Line: String;
  P: Integer;
begin
  Result := '';
  Output := Output + #10;
  while Output <> '' do
  begin
    P := Pos(#10, Output);
    Line := Trim(Copy(Output, 1, P - 1));
    Delete(Output, 1, P);
    if Pos('warning:', Line) = 1 then Result := Result + Trim(Copy(Line, 9, Length(Line))) + #13#10;
  end;
end;

{ Fills the folder page from "xpplens detect" (lines: path TAB packages TAB source). }
procedure DetectPackages;
var
  Exe, OutFile, Line, Found: String;
  Lines: TArrayOfString;
  I, P: Integer;
  Code: Integer;
begin
  Detected := True;
  ExtractTemporaryFile('xpplens.exe');
  Exe := ExpandConstant('{tmp}\xpplens.exe');
  OutFile := ExpandConstant('{tmp}\xpplens-detect.txt');
  DeleteFile(OutFile);
  Exec(ExpandConstant('{cmd}'), '/C ""' + Exe + '" detect > "' + OutFile + '" 2>&1"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if not LoadStringsFromFile(OutFile, Lines) then SetArrayLength(Lines, 0);
  Found := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    P := Pos(#9, Line);
    if P > 1 then
    begin
      if PkgPage.Values[0] = '' then PkgPage.Values[0] := Copy(Line, 1, P - 1);
      StringChangeEx(Line, #9, '   ', True);
      Found := Found + Line + #13#10;
    end;
  end;
  if Found = '' then
    PkgInfo.Caption := CustomMessage('PkgNone')
  else
    PkgInfo.Caption := FmtMessage(CustomMessage('PkgFound'), [Found]);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := HasSettings and ((PageID = PkgPage.ID) or (PageID = LangPage.ID) or (PageID = StdPage.ID));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = PkgPage.ID) and not Detected then
  begin
    WizardForm.NextButton.Enabled := False;
    WizardForm.Repaint;
    try
      DetectPackages;
    finally
      WizardForm.NextButton.Enabled := True;
    end;
  end;
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + CustomMessage('Registered');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = PkgPage.ID) and (CountPackages(PkgPage.Values[0]) < 3) then
  begin
    MsgBox(FmtMessage(CustomMessage('PkgInvalid'), [PkgPage.Values[0]]), mbError, MB_OK);
    Result := False;
  end;
end;

{ A running xpplens.exe and the e_sqlite3.dll it loaded (an open Claude session) cannot be overwritten but can be
  renamed; xpplens removes *.old files when it starts. Whether this is an update is decided here, before the files
  are copied. }
procedure RenameInUse(Pattern: String);
var
  Bin, Stamp: String;
  Found: TFindRec;
  Names: TArrayOfString;
  I: Integer;
begin
  Bin := ExpandConstant('{app}\bin\');
  Stamp := GetDateTimeString('yyyymmddhhnnss', #0, #0);
  { Names first, renames after: renaming while the folder is being listed could list a file twice. }
  if FindFirst(Bin + Pattern, Found) then
  try
    repeat
      if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
      begin
        SetArrayLength(Names, GetArrayLength(Names) + 1);
        Names[GetArrayLength(Names) - 1] := Found.Name;
      end;
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
  for I := 0 to GetArrayLength(Names) - 1 do
    RenameFile(Bin + Names[I], Bin + Names[I] + '.' + Stamp + '.old');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  WasUpdate := FileExists(ExpandConstant('{app}\xpplens.json'));
  RenameInUse('*.exe');
  RenameInUse('*.dll');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Exe, Params, Langs, Output: String;
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  Exe := ExpandConstant('{app}\bin\xpplens.exe');
  if not WasUpdate then
  begin
    Langs := Trim(LangPage.Values[0]);
    StringChangeEx(Langs, ' ', '', True);
    StringChangeEx(Langs, ';', ',', True);
    StringChangeEx(Langs, '"', '', True);
    if Langs = '' then Langs := 'en-US';
    Params := 'config --packages-dir "' + RemoveBackslashUnlessRoot(PkgPage.Values[0]) + '" --languages "' + Langs + '"';
    if not StdPage.Values[0] then Params := Params + ' --index-standard false';
    Code := RunXpp(Exe, Params, Output);
    if (Code <> 0) and not WizardSilent then MsgBox(FmtMessage(CustomMessage('ConfigFailed'), [Output]), mbError, MB_OK);
  end;
  { Registration in Claude Desktop and Claude Code (an update re-registers the same path). }
  RunXpp(Exe, 'register', Output);
  { Claude Code skills; one the user changed is kept, and said so (no message box in a silent update). }
  if SkillsPage.Values[0] then
  begin
    { In the wizard's language (a silent update runs in the language of the last installation). }
    RunXpp(Exe, 'skills install --lang ' + ActiveLanguage, Output);
    Output := SkillWarnings(Output);
    if (Output <> '') and not WizardSilent then MsgBox(FmtMessage(CustomMessage('SkillsKept'), [Output]), mbInformation, MB_OK);
  end;
  { Silent update with /RESTARTCLAUDE=1 (xpplens update --install --restart-claude). }
  if ExpandConstant('{param:RESTARTCLAUDE|0}') = '1' then
    Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ExpandConstant('{app}\restart-claude.ps1') + '" -Version {#Version}',
      '', SW_HIDE, ewNoWait, Code);
end;

{ Before the files go: ask whether to remove the index and settings (default: keep them). After: take the program
  folder off the PATH and delete the data when asked to. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Output: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveUserData := not UninstallSilent and
      (MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES);
    { Only the skills xpp-lens installed and nobody changed; a changed one stays where it is. }
    RunXpp(ExpandConstant('{app}\bin\xpplens.exe'), 'skills remove', Output);
    Output := SkillWarnings(Output);
    if (Output <> '') and not UninstallSilent then MsgBox(Output, mbInformation, MB_OK);
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    RemoveFromPath(ExpandConstant('{app}\bin'));
    if RemoveUserData then
    begin
      DelTree(ExpandConstant('{localappdata}\xpp-lens'), True, True, True);
      DeleteFile(ExpandConstant('{app}\xpplens.json'));
      RemoveDir(ExpandConstant('{app}\bin'));
      RemoveDir(ExpandConstant('{app}'));
    end;
  end;
end;
