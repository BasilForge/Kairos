; Kairos's install wizard: language, welcome, folder, options, install, finish.
; It carries Velopack's one-click Setup and runs it silently into the chosen folder, so Velopack still owns the
; install (updates, the Apps & features entry, the uninstaller); the wizard itself installs nothing to uninstall.
; Built by tools\release.ps1:  ISCC /DAppVersion=1.0.0 /DPackId=Kairos /DOutDir=<releases dir> Kairos.iss
; (it takes <OutDir>\<PackId>-win-Setup.exe, made there by vpk pack just before).
; A test build (another PackId) writes its choices file and start-with-Windows entry under its own name, away from
; the real Kairos's (a test never starts its app).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PackId
  #define PackId "Kairos"
#endif
#ifndef OutDir
  #define OutDir "..\releases"
#endif
#define VeloSetup OutDir + "\" + PackId + "-win-Setup.exe"
; Microsoft's own Visual C++ runtime installer (the full, offline one), put in by tools\release.ps1: installed by the
; wizard itself only on a PC without it (14.50 or newer), so a PC that has it never sees or installs it again.
#ifndef VcRedist
  #define VcRedist "redist\vc_redist.x64.exe"
#endif
#if !FileExists(AddBackslash(SourcePath) + VcRedist) && !FileExists(VcRedist)
  #error Microsoft's vc_redist.x64.exe is missing: tools\release.ps1 puts it in installer\redist
#endif
; About what an install takes on its drive: the app (about twice the package) and the package Velopack keeps for updates;
; and the Setup itself in the temporary folder while it runs.
#define NeedMB FileSize(VeloSetup) / 1048576 * 4
#define TempMB FileSize(VeloSetup) / 1048576

[Setup]
AppId={#PackId}.Wizard
AppName={#PackId}
AppVersion={#AppVersion}
AppVerName={#PackId} {#AppVersion}
AppPublisher=Kairos
AppPublisherURL=https://github.com/BasilForge/Kairos
DefaultDirName={code:DefaultDir}
UsePreviousAppDir=no
; The language Kairos keeps under this AppId's key (WizardLanguage): installing over Kairos opens in its language.
UsePreviousLanguage=yes
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
DirExistsWarning=no
PrivilegesRequired=lowest
SetupLogging=yes
Uninstallable=no
CloseApplications=no
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern dark
WizardImageFile=wizard.png
WizardSmallImageFile=wizard-small.png
SetupIconFile=..\src\Kairos\app.ico
OutputDir={#OutDir}
OutputBaseFilename={#PackId}-Setup
; Velopack's Setup is mostly compressed already (LZMA would save about 6% and slow the install).
Compression=none

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "zhHans"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "ptBR"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.LangCode=en
ar.LangCode=ar
zhHans.LangCode=zh-Hans
ru.LangCode=ru
es.LangCode=es
ptBR.LangCode=pt-BR
de.LangCode=de
en.StartWithWindows=Start Kairos with Windows, so instant replay is ready when you play
ar.StartWithWindows=تشغيل Kairos مع بدء ويندوز، لتكون الإعادة الفورية جاهزة حين تلعب
zhHans.StartWithWindows=随 Windows 启动 Kairos，玩游戏时即时回放已就绪
ru.StartWithWindows=Запускать Kairos вместе с Windows, чтобы мгновенный повтор был готов, когда вы играете
es.StartWithWindows=Iniciar Kairos con Windows, para que la repetición instantánea esté lista cuando juegue
ptBR.StartWithWindows=Iniciar o Kairos com o Windows, para o replay instantâneo estar pronto quando você jogar
de.StartWithWindows=Kairos mit Windows starten, damit Instant Replay beim Spielen bereit ist
en.InstallingApp=Installing Kairos…
ar.InstallingApp=جارٍ تثبيت Kairos…
zhHans.InstallingApp=正在安装 Kairos…
ru.InstallingApp=Установка Kairos…
es.InstallingApp=Instalando Kairos…
ptBR.InstallingApp=Instalando o Kairos…
de.InstallingApp=Kairos wird installiert…
en.KairosRunning=Kairos is running. Quit it first (right-click its icon by the clock, then Exit), then choose Retry.
ar.KairosRunning=يعمل Kairos الآن. أغلقه أولًا (انقر بزر الفأرة الأيمن على أيقونته بجوار الساعة، ثم «خروج»)، ثم اختر «إعادة المحاولة».
zhHans.KairosRunning=Kairos 正在运行。请先退出它（右键单击时钟旁的图标，然后选择“退出”），再选择“重试”。
ru.KairosRunning=Kairos запущен. Сначала закройте его (щёлкните правой кнопкой по его значку у часов и выберите «Выход»), затем нажмите «Повторить».
es.KairosRunning=Kairos está abierto. Ciérrelo primero (clic derecho en el icono de Kairos junto al reloj y luego «Salir») y después elija Reintentar.
ptBR.KairosRunning=O Kairos está aberto. Feche-o primeiro (clique com o botão direito no ícone ao lado do relógio e depois em “Sair”) e escolha Repetir.
de.KairosRunning=Kairos läuft. Beenden Sie es zuerst (Rechtsklick auf sein Symbol neben der Uhr, dann „Beenden“) und wählen Sie dann Wiederholen.
en.InstallFailed=Kairos could not be installed (error %1).
ar.InstallFailed=تعذّر تثبيت Kairos (الخطأ %1).
zhHans.InstallFailed=无法安装 Kairos（错误 %1）。
ru.InstallFailed=Не удалось установить Kairos (ошибка %1).
es.InstallFailed=No se pudo instalar Kairos (error %1).
ptBR.InstallFailed=Não foi possível instalar o Kairos (erro %1).
de.InstallFailed=Kairos konnte nicht installiert werden (Fehler %1).
en.CauseRuntime=Kairos needs Microsoft Visual C++. Install it from learn.microsoft.com/cpp/windows/latest-supported-vc-redist, then run this setup again.
ar.CauseRuntime=يحتاج Kairos إلى Microsoft Visual C++. ثبّته من learn.microsoft.com/cpp/windows/latest-supported-vc-redist، ثم شغّل هذا المثبّت مرة أخرى.
zhHans.CauseRuntime=Kairos 需要 Microsoft Visual C++。请从 learn.microsoft.com/cpp/windows/latest-supported-vc-redist 安装它，然后重新运行本安装程序。
ru.CauseRuntime=Для работы Kairos нужен Microsoft Visual C++. Установите его с learn.microsoft.com/cpp/windows/latest-supported-vc-redist, затем снова запустите установку.
es.CauseRuntime=Kairos necesita Microsoft Visual C++. Instálelo desde learn.microsoft.com/cpp/windows/latest-supported-vc-redist y vuelva a ejecutar este instalador.
ptBR.CauseRuntime=O Kairos precisa do Microsoft Visual C++. Instale-o em learn.microsoft.com/cpp/windows/latest-supported-vc-redist e execute este instalador de novo.
de.CauseRuntime=Kairos benötigt Microsoft Visual C++. Installieren Sie es von learn.microsoft.com/cpp/windows/latest-supported-vc-redist und starten Sie dieses Setup dann erneut.
en.CauseBlocked=Windows or your antivirus blocked a file Setup needed. Allow Kairos in your antivirus (or pause it for a minute), then run this setup again.
ar.CauseBlocked=حجب ويندوز أو برنامج مكافحة الفيروسات ملفًا يحتاجه المثبّت. اسمح لـ Kairos في برنامج الحماية (أو أوقفه دقيقة)، ثم شغّل هذا المثبّت مرة أخرى.
zhHans.CauseBlocked=Windows 或杀毒软件拦截了安装所需的文件。请在杀毒软件中允许 Kairos（或将其暂停一分钟），然后重新运行本安装程序。
ru.CauseBlocked=Windows или антивирус заблокировали нужный установщику файл. Разрешите Kairos в антивирусе (или приостановите его на минуту), затем снова запустите установку.
es.CauseBlocked=Windows o su antivirus bloqueó un archivo que el instalador necesitaba. Permita Kairos en su antivirus (o pause el antivirus un minuto) y vuelva a ejecutar este instalador.
ptBR.CauseBlocked=O Windows ou seu antivírus bloqueou um arquivo de que o instalador precisava. Permita o Kairos no antivírus (ou pause-o por um minuto) e execute este instalador de novo.
de.CauseBlocked=Windows oder Ihr Virenschutz hat eine Datei blockiert, die das Setup benötigt. Erlauben Sie Kairos in Ihrem Virenschutz (oder pausieren Sie ihn eine Minute) und starten Sie dieses Setup dann erneut.
en.CauseInUse=A file in this folder is in use. Close Kairos and any window open in that folder (or restart the PC), then run this setup again.
ar.CauseInUse=ملف في هذا المجلد قيد الاستخدام. أغلق Kairos وأي نافذة مفتوحة في ذلك المجلد (أو أعد تشغيل الجهاز)، ثم شغّل هذا المثبّت مرة أخرى.
zhHans.CauseInUse=此文件夹中的某个文件正在使用中。请关闭 Kairos 以及在该文件夹中打开的窗口（或重启电脑），然后重新运行本安装程序。
ru.CauseInUse=Файл в этой папке используется. Закройте Kairos и все окна, открытые в этой папке (или перезагрузите ПК), затем снова запустите установку.
es.CauseInUse=Un archivo de esta carpeta está en uso. Cierre Kairos y cualquier ventana abierta en esa carpeta (o reinicie el PC) y vuelva a ejecutar este instalador.
ptBR.CauseInUse=Um arquivo nesta pasta está em uso. Feche o Kairos e qualquer janela aberta nessa pasta (ou reinicie o PC) e execute este instalador de novo.
de.CauseInUse=Eine Datei in diesem Ordner wird verwendet. Schließen Sie Kairos und alle Fenster in diesem Ordner (oder starten Sie den PC neu) und starten Sie dieses Setup dann erneut.
en.CauseSpace=The drive is full. Free some space or choose another folder, then run this setup again.
ar.CauseSpace=القرص ممتلئ. أفرغ بعض المساحة أو اختر مجلدًا آخر، ثم شغّل هذا المثبّت مرة أخرى.
zhHans.CauseSpace=磁盘已满。请释放一些空间或选择其他文件夹，然后重新运行本安装程序。
ru.CauseSpace=Диск заполнен. Освободите место или выберите другую папку, затем снова запустите установку.
es.CauseSpace=El disco está lleno. Libere espacio o elija otra carpeta y vuelva a ejecutar este instalador.
ptBR.CauseSpace=O disco está cheio. Libere espaço ou escolha outra pasta e execute este instalador de novo.
de.CauseSpace=Das Laufwerk ist voll. Schaffen Sie Platz oder wählen Sie einen anderen Ordner und starten Sie dieses Setup dann erneut.
en.CauseOther=Run this setup again. If it fails again, send us the log on Discord (discord.gg/YWgaS6JRzF).
ar.CauseOther=شغّل هذا المثبّت مرة أخرى. وإن فشل ثانيةً، أرسل لنا السجل في ديسكورد (discord.gg/YWgaS6JRzF).
zhHans.CauseOther=请重新运行本安装程序。如果仍然失败，请在 Discord（discord.gg/YWgaS6JRzF）把日志发给我们。
ru.CauseOther=Запустите установку ещё раз. Если ошибка повторится, пришлите нам журнал в Discord (discord.gg/YWgaS6JRzF).
es.CauseOther=Vuelva a ejecutar este instalador. Si falla otra vez, envíenos el registro en Discord (discord.gg/YWgaS6JRzF).
ptBR.CauseOther=Execute este instalador de novo. Se falhar outra vez, envie-nos o registro no Discord (discord.gg/YWgaS6JRzF).
de.CauseOther=Starten Sie dieses Setup erneut. Wenn es wieder fehlschlägt, schicken Sie uns das Protokoll auf Discord (discord.gg/YWgaS6JRzF).
en.LogTail=The setup log ends with:
ar.LogTail=آخر ما في سجل التثبيت:
zhHans.LogTail=安装日志的最后几行：
ru.LogTail=Конец журнала установки:
es.LogTail=El registro de instalación termina con:
ptBR.LogTail=O registro da instalação termina com:
de.LogTail=Das Setup-Protokoll endet mit:
en.OpenLog=The full log is %1%nOpen it now?
ar.OpenLog=السجل الكامل في %1%nهل تريد فتحه الآن؟
zhHans.OpenLog=完整日志位于 %1%n现在打开吗？
ru.OpenLog=Полный журнал: %1%nОткрыть его сейчас?
es.OpenLog=El registro completo está en %1%n¿Abrirlo ahora?
ptBR.OpenLog=O registro completo está em %1%nAbrir agora?
de.OpenLog=Das vollständige Protokoll liegt unter %1%nJetzt öffnen?
en.NotEnoughSpace=There isn't enough free space on %1: Kairos needs about %2 MB. Free some space or choose another drive.
ar.NotEnoughSpace=لا توجد مساحة كافية على %1: يحتاج Kairos إلى نحو %2 ميجابايت. أفرغ بعض المساحة أو اختر قرصًا آخر.
zhHans.NotEnoughSpace=%1 上的可用空间不足：Kairos 需要约 %2 MB。请释放一些空间或选择其他磁盘。
ru.NotEnoughSpace=Недостаточно места на %1: Kairos нужно около %2 МБ. Освободите место или выберите другой диск.
es.NotEnoughSpace=No hay espacio libre suficiente en %1: Kairos necesita unos %2 MB. Libere espacio o elija otra unidad.
ptBR.NotEnoughSpace=Não há espaço livre suficiente em %1: o Kairos precisa de cerca de %2 MB. Libere espaço ou escolha outra unidade.
de.NotEnoughSpace=Auf %1 ist nicht genug Platz frei: Kairos benötigt etwa %2 MB. Schaffen Sie Platz oder wählen Sie ein anderes Laufwerk.
en.VcReady=Microsoft Visual C++ (needed by Kairos) is not on this PC: Setup installs it first, and Windows will ask for permission.
ar.VcReady=Microsoft Visual C++ (يحتاجه Kairos) غير مثبّت على هذا الجهاز: سيثبّته المثبّت أولًا، وسيطلب ويندوز الإذن.
zhHans.VcReady=此电脑上没有 Microsoft Visual C++（Kairos 需要它）：安装程序会先安装它，Windows 会请求权限。
ru.VcReady=На этом компьютере нет Microsoft Visual C++ (он нужен Kairos): установщик сначала установит его, Windows запросит разрешение.
es.VcReady=Este PC no tiene Microsoft Visual C++ (Kairos lo necesita): el instalador lo instala primero y Windows le pedirá permiso.
ptBR.VcReady=Este PC não tem o Microsoft Visual C++ (o Kairos precisa dele): o instalador o instala primeiro e o Windows vai pedir permissão.
de.VcReady=Auf diesem PC fehlt Microsoft Visual C++ (Kairos benötigt es): Das Setup installiert es zuerst, und Windows fragt nach Ihrer Erlaubnis.
en.VcDeclined=Kairos needs Microsoft Visual C++, and Windows was not allowed to install it. Run this setup again and choose Yes, or install it from learn.microsoft.com/cpp/windows/latest-supported-vc-redist.
ar.VcDeclined=يحتاج Kairos إلى Microsoft Visual C++، ولم يُسمح لويندوز بتثبيته. شغّل هذا المثبّت مرة أخرى واختر «نعم»، أو ثبّته من learn.microsoft.com/cpp/windows/latest-supported-vc-redist.
zhHans.VcDeclined=Kairos 需要 Microsoft Visual C++，但未允许 Windows 安装它。请重新运行本安装程序并选择“是”，或从 learn.microsoft.com/cpp/windows/latest-supported-vc-redist 安装。
ru.VcDeclined=Для работы Kairos нужен Microsoft Visual C++, но Windows не разрешили его установить. Запустите установку снова и выберите «Да» или установите его с learn.microsoft.com/cpp/windows/latest-supported-vc-redist.
es.VcDeclined=Kairos necesita Microsoft Visual C++ y no se permitió a Windows instalarlo. Vuelva a ejecutar este instalador y elija Sí, o instálelo desde learn.microsoft.com/cpp/windows/latest-supported-vc-redist.
ptBR.VcDeclined=O Kairos precisa do Microsoft Visual C++ e o Windows não teve permissão para instalá-lo. Execute este instalador de novo e escolha Sim, ou instale-o em learn.microsoft.com/cpp/windows/latest-supported-vc-redist.
de.VcDeclined=Kairos benötigt Microsoft Visual C++, aber Windows durfte es nicht installieren. Starten Sie dieses Setup erneut und wählen Sie Ja, oder installieren Sie es von learn.microsoft.com/cpp/windows/latest-supported-vc-redist.
en.VcFailed=Microsoft Visual C++ could not be installed (code %1). Restart the PC and run this setup again, or install it from learn.microsoft.com/cpp/windows/latest-supported-vc-redist.
ar.VcFailed=تعذّر تثبيت Microsoft Visual C++ (الرمز %1). أعد تشغيل الجهاز وشغّل هذا المثبّت مرة أخرى، أو ثبّته من learn.microsoft.com/cpp/windows/latest-supported-vc-redist.
zhHans.VcFailed=无法安装 Microsoft Visual C++（代码 %1）。请重启电脑后重新运行本安装程序，或从 learn.microsoft.com/cpp/windows/latest-supported-vc-redist 安装。
ru.VcFailed=Не удалось установить Microsoft Visual C++ (код %1). Перезагрузите компьютер и снова запустите установку или установите его с learn.microsoft.com/cpp/windows/latest-supported-vc-redist.
es.VcFailed=No se pudo instalar Microsoft Visual C++ (código %1). Reinicie el PC y vuelva a ejecutar este instalador, o instálelo desde learn.microsoft.com/cpp/windows/latest-supported-vc-redist.
ptBR.VcFailed=Não foi possível instalar o Microsoft Visual C++ (código %1). Reinicie o PC e execute este instalador de novo, ou instale-o em learn.microsoft.com/cpp/windows/latest-supported-vc-redist.
de.VcFailed=Microsoft Visual C++ konnte nicht installiert werden (Code %1). Starten Sie den PC neu und führen Sie dieses Setup erneut aus, oder installieren Sie es von learn.microsoft.com/cpp/windows/latest-supported-vc-redist.
en.VcBusy=Windows is installing something else right now (often Windows Update), so Microsoft Visual C++ could not be installed. Wait for it to finish (or restart the PC), then run this setup again.
ar.VcBusy=يثبّت ويندوز شيئًا آخر الآن (غالبًا تحديثات ويندوز)، فتعذّر تثبيت Microsoft Visual C++. انتظر حتى ينتهي (أو أعد تشغيل الجهاز)، ثم شغّل هذا المثبّت مرة أخرى.
zhHans.VcBusy=Windows 正在安装其他内容（通常是 Windows 更新），因此无法安装 Microsoft Visual C++。请等待其完成（或重启电脑），然后重新运行本安装程序。
ru.VcBusy=Windows сейчас устанавливает что-то другое (часто обновления Windows), поэтому Microsoft Visual C++ не удалось установить. Дождитесь окончания (или перезагрузите компьютер) и снова запустите установку.
es.VcBusy=Windows está instalando otra cosa ahora mismo (a menudo Windows Update), así que no se pudo instalar Microsoft Visual C++. Espere a que termine (o reinicie el PC) y vuelva a ejecutar este instalador.
ptBR.VcBusy=O Windows está instalando outra coisa agora (geralmente o Windows Update), então não foi possível instalar o Microsoft Visual C++. Espere terminar (ou reinicie o PC) e execute este instalador de novo.
de.VcBusy=Windows installiert gerade etwas anderes (oft Windows Update), daher konnte Microsoft Visual C++ nicht installiert werden. Warten Sie, bis es fertig ist (oder starten Sie den PC neu), und starten Sie dieses Setup dann erneut.
en.VcOpenPage=Open Microsoft's download page now?
ar.VcOpenPage=هل تريد فتح صفحة التنزيل من مايكروسوفت الآن؟
zhHans.VcOpenPage=现在打开 Microsoft 的下载页面吗？
ru.VcOpenPage=Открыть страницу загрузки Microsoft сейчас?
es.VcOpenPage=¿Abrir ahora la página de descarga de Microsoft?
ptBR.VcOpenPage=Abrir agora a página de download da Microsoft?
de.VcOpenPage=Die Downloadseite von Microsoft jetzt öffnen?
en.InstallingRuntime=Installing Microsoft Visual C++ for Kairos (Windows may ask for permission)…
ar.InstallingRuntime=جارٍ تثبيت Microsoft Visual C++ لـ Kairos (قد يطلب ويندوز الإذن)…
zhHans.InstallingRuntime=正在为 Kairos 安装 Microsoft Visual C++（Windows 可能会请求权限）…
ru.InstallingRuntime=Установка Microsoft Visual C++ для Kairos (Windows может запросить разрешение)…
es.InstallingRuntime=Instalando Microsoft Visual C++ para Kairos (Windows puede pedir permiso)…
ptBR.InstallingRuntime=Instalando o Microsoft Visual C++ para o Kairos (o Windows pode pedir permissão)…
de.InstallingRuntime=Microsoft Visual C++ für Kairos wird installiert (Windows fragt eventuell nach Erlaubnis)…
en.FolderNotEmpty=Please choose an empty or new folder. Uninstalling Kairos removes its whole folder, so it must not share one with other files.
ar.FolderNotEmpty=يُرجى اختيار مجلد فارغ أو جديد. إزالة Kairos تحذف مجلده كاملًا، لذا يجب ألا يحوي ملفات أخرى.
zhHans.FolderNotEmpty=请选择一个空文件夹或新文件夹。卸载 Kairos 会删除其整个文件夹，因此该文件夹不能包含其他文件。
ru.FolderNotEmpty=Выберите пустую или новую папку. При удалении Kairos удаляется вся его папка, поэтому в ней не должно быть других файлов.
es.FolderNotEmpty=Elija una carpeta vacía o nueva. Al desinstalar Kairos se borra toda la carpeta del programa, así que no debe compartirla con otros archivos.
ptBR.FolderNotEmpty=Escolha uma pasta vazia ou nova. Desinstalar o Kairos apaga a pasta inteira, então ela não pode ter outros arquivos.
de.FolderNotEmpty=Bitte wählen Sie einen leeren oder neuen Ordner. Beim Deinstallieren entfernt Kairos seinen ganzen Ordner, er darf also keine anderen Dateien enthalten.
en.FolderNotWritable=Kairos cannot write to this folder. Choose one in your user folder or on another drive (folders such as Program Files need administrator rights).
ar.FolderNotWritable=لا يستطيع Kairos الكتابة في هذا المجلد. اختر مجلدًا داخل مجلد المستخدم أو على قرص آخر (مجلدات مثل Program Files تحتاج صلاحيات المسؤول).
zhHans.FolderNotWritable=Kairos 无法写入此文件夹。请选择用户文件夹中或其他磁盘上的文件夹（Program Files 等文件夹需要管理员权限）。
ru.FolderNotWritable=Kairos не может записывать в эту папку. Выберите папку в своём профиле или на другом диске (для папок вроде Program Files нужны права администратора).
es.FolderNotWritable=Kairos no puede escribir en esta carpeta. Elija una en su carpeta de usuario o en otra unidad (carpetas como Archivos de programa requieren permisos de administrador).
ptBR.FolderNotWritable=O Kairos não consegue gravar nesta pasta. Escolha uma na sua pasta de usuário ou em outra unidade (pastas como Arquivos de Programas exigem direitos de administrador).
de.FolderNotWritable=Kairos kann in diesen Ordner nicht schreiben. Wählen Sie einen Ordner in Ihrem Benutzerordner oder auf einem anderen Laufwerk (Ordner wie Programme erfordern Administratorrechte).

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"
Name: "autostart"; Description: "{cm:StartWithWindows}"

[Files]
; Run from [Code] once the progress page shows; Velopack's Setup extracts and installs the app itself.
Source: "{#VeloSetup}"; DestName: "{#PackId}-win-Setup.exe"; Flags: dontcopy
; Extracted and run only on a PC without the runtime (NeedsVcRuntime).
Source: "{#VcRedist}"; DestName: "vc_redist.x64.exe"; Flags: dontcopy

[Icons]
; Velopack's uninstaller removes every shortcut that points into the install folder, this one too.
Name: "{userdesktop}\{#PackId}"; Filename: "{app}\{#PackId}.exe"; Tasks: desktopicon

[Registry]
; The same entry Kairos keeps in step with its setting (Autostart.CommandFor, AppInfo.RunValueName): the wizard writes
; the first one, or clears an old one, for when Kairos is not started from the last page.
; {#PackId}.exe is Velopack's launcher at the top of the install folder (named after the package).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#PackId}"; ValueData: """{app}\{#PackId}.exe"" --background"; Tasks: autostart
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#PackId}"; Flags: deletevalue; Tasks: not autostart

[Run]
Filename: "{app}\{#PackId}.exe"; Description: "{cm:LaunchProgram,{#PackId}}"; Flags: nowait postinstall skipifsilent

[Code]
{ Message boxes are suppressible: a silent install (/VERYSILENT /SUPPRESSMSGBOXES) never shows one, it takes the default. }
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#PackId}';
  WizardKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#PackId}.Wizard_is1';

var
  { Installing over Kairos: what it has already, so the wizard passes on only what the user changes. }
  HadSettings, SeededAutostart, TasksSeeded: Boolean;
  PrevLang: String;

{ Kairos's settings file (start with Windows; one that cannot be read counts as on) and the language it left for the
  wizard (none from Kairos 1.0). }
procedure InitializeWizard();
var
  Raw: AnsiString;
  Text: String;
begin
  HadSettings := LoadStringFromFile(ExpandConstant('{userappdata}\{#PackId}\settings.json'), Raw);
  SeededAutostart := True;
  if HadSettings then
  begin
    Text := Lowercase(String(Raw));
    StringChangeEx(Text, ' ', '', True);
    SeededAutostart := Pos('"startwithwindows":false', Text) = 0;
  end;
  RegQueryStringValue(HKCU, WizardKey, 'Inno Setup: Language', PrevLang); { none: stays '' }
end;

{ The value of /<Name>= on the command line, without quotes; Found says whether it is there at all. }
function CommandLineValue(Name: String; var Found: Boolean): String;
var
  I: Integer;
  Param: String;
begin
  Result := '';
  Found := False;
  for I := 1 to ParamCount do
  begin
    Param := ParamStr(I);
    if Pos('/' + Uppercase(Name) + '=', Uppercase(Param)) = 1 then
    begin
      Found := True;
      Result := RemoveQuotes(Copy(Param, Length(Name) + 3, MaxInt));
    end;
  end;
end;

{ Over Kairos, the options start as Kairos has them: start with Windows from its settings, the desktop shortcut if one is
  there. /TASKS= is the user's whole choice and replaces that; /MERGETASKS= changes only the options it names. }
procedure CurPageChanged(CurPageID: Integer);
var
  Given: Boolean;
  Merge: String;
begin
  if (CurPageID <> wpSelectTasks) or TasksSeeded then
    Exit;
  TasksSeeded := True;
  CommandLineValue('TASKS', Given);
  if not HadSettings or Given then
    Exit;
  if SeededAutostart then
    WizardSelectTasks('autostart')
  else
    WizardSelectTasks('!autostart');
  if FileExists(ExpandConstant('{userdesktop}\{#PackId}.lnk')) then
    WizardSelectTasks('desktopicon')
  else
    WizardSelectTasks('!desktopicon');
  Merge := CommandLineValue('MERGETASKS', Given);
  if Given and (Merge <> '') then
    WizardSelectTasks(Merge);
end;

{ The folder Kairos is in already (to install over it), or the usual one. }
function DefaultDir(Param: String): String;
begin
  if not RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', Result) or (Result = '') then
    Result := ExpandConstant('{localappdata}\{#PackId}');
  Result := RemoveBackslashUnlessRoot(Result);
end;

function ChosenDir(): String;
begin
  Result := RemoveBackslashUnlessRoot(WizardDirValue());
end;

{ A Velopack install of Kairos, as InstallPaths.RootFor sees one. }
function IsKairosInstall(Dir: String): Boolean;
begin
  Result := FileExists(AddBackslash(Dir) + 'Update.exe') and DirExists(AddBackslash(Dir) + 'current');
end;

{ Empty, or only what Velopack puts there (an install that stopped half way: Setup replaces it). }
function HoldsNothingElse(Dir: String): Boolean;
var
  Found: TFindRec;
  Name: String;
begin
  Result := True;
  if FindFirst(AddBackslash(Dir) + '*', Found) then
    try
      repeat
        Name := Lowercase(Found.Name);
        if (Name <> '.') and (Name <> '..') and (Name <> 'update.exe') and (Name <> 'current') and (Name <> 'packages')
            and (Name <> Lowercase('{#PackId}.exe')) and (Name <> '.betaid') and (Name <> 'sq.version') then
        begin
          Result := False;
          Exit;
        end;
      until not FindNext(Found);
    finally
      FindClose(Found);
    end;
end;

function CanWriteTo(Dir: String): Boolean;
var
  Existed: Boolean;
  Probe: String;
begin
  Existed := DirExists(Dir);
  Result := ForceDirectories(Dir);
  if Result then
  begin
    Probe := AddBackslash(Dir) + '.kairos-setup-probe';
    Result := SaveStringToFile(Probe, 'ok', False);
    DeleteFile(Probe);
    if not Existed then
      RemoveDir(Dir);
  end;
end;

{ At least MB free on the path's drive (unknown counts as room). }
function HasRoom(Path: String; MB: Integer): Boolean;
var
  Free, Total: Int64;
begin
  Result := not GetSpaceOnDisk64(ExtractFileDrive(Path) + '\', Free, Total) or (Free div (1024 * 1024) >= MB);
end;

{ Room for Kairos on the chosen drive and for its Setup in the temporary folder (both on one drive: the sum); when not,
  the drive and the MB it needs. }
function EnoughSpace(Dir: String; var Drive: String; var Need: Integer): Boolean;
var
  Temp: String;
  SameDrive: Boolean;
begin
  Temp := GetTempDir();
  Drive := ExtractFileDrive(Dir);
  SameDrive := CompareText(ExtractFileDrive(Temp), Drive) = 0;
  Need := {#NeedMB};
  if SameDrive then
    Need := Need + {#TempMB};
  Result := HasRoom(Dir, Need);
  if Result and not SameDrive then
  begin
    Drive := ExtractFileDrive(Temp);
    Need := {#TempMB};
    Result := HasRoom(Temp, Need);
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir, Drive: String;
  Need: Integer;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Dir := ChosenDir();
    { Uninstalling removes the whole folder: never a drive's root, never a folder that holds other things. }
    if (Length(Dir) <= 3) or (DirExists(Dir) and not IsKairosInstall(Dir) and not HoldsNothingElse(Dir)) then
    begin
      SuppressibleMsgBox(CustomMessage('FolderNotEmpty'), mbError, MB_OK, IDOK);
      Result := False;
    end
    else if not CanWriteTo(Dir) then
    begin
      SuppressibleMsgBox(CustomMessage('FolderNotWritable'), mbError, MB_OK, IDOK);
      Result := False;
    end
    else if not EnoughSpace(Dir, Drive, Need) then
    begin
      SuppressibleMsgBox(FmtMessage(CustomMessage('NotEnoughSpace'), [Drive, IntToStr(Need)]), mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

{ A Kairos started from the chosen folder (another copy elsewhere, a test or a developer's, does not count). }
function KairosRunningIn(Dir: String): Boolean;
var
  Locator, Service, Found, Item: Variant;
  I: Integer;
  ExePath: String;
begin
  Result := False;
  if not IsKairosInstall(Dir) then
    Exit; { a new or empty folder: nothing can be running from it }
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Service.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = ''Kairos.exe''');
    for I := 0 to Found.Count - 1 do
    begin
      Item := Found.ItemIndex(I);
      { A process Windows does not show the path of (another user's) has no ExecutablePath. }
      if VarIsNull(Item.ExecutablePath) then
        Continue;
      ExePath := Item.ExecutablePath;
      if Pos(Lowercase(AddBackslash(Dir)), Lowercase(ExePath)) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  except
    { No WMI: go on (the Setup itself still copes with a running copy). }
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  while KairosRunningIn(ChosenDir()) do
    if SuppressibleMsgBox(CustomMessage('KairosRunning'), mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := CustomMessage('KairosRunning');
      Exit;
    end;
end;

{ For Kairos's next start: a first install's choices, or, over Kairos, only the ones the user changed (none: no file). }
procedure WriteChoices();
var
  DataDir, Choices: String;
  AutostartOn: Boolean;
begin
  Choices := '';
  if not HadSettings or ((PrevLang <> '') and (PrevLang <> ActiveLanguage())) then
    Choices := 'language=' + CustomMessage('LangCode') + #13#10;
  AutostartOn := WizardIsTaskSelected('autostart');
  if not HadSettings or (AutostartOn <> SeededAutostart) then
  begin
    Choices := Choices + 'startWithWindows=';
    if AutostartOn then
      Choices := Choices + 'true' + #13#10
    else
      Choices := Choices + 'false' + #13#10;
  end;
  if Choices = '' then
    Exit;
  DataDir := ExpandConstant('{userappdata}\{#PackId}');
  ForceDirectories(DataDir);
  SaveStringToFile(AddBackslash(DataDir) + 'install-choices.txt', Choices, False);
end;

{ The Visual C++ runtime libobs needs (14.50 or newer), from Windows' own record of it; the wizard installs it when missing. }
function VcRuntimeMissing(): Boolean;
var
  Major, Minor: Cardinal;
begin
  Result := not RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64', 'Major', Major)
    or not RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64', 'Minor', Minor)
    or ((Major = 14) and (Minor < 50));
end;

var
  { /FORCEVC=1: the wizard acts as on a PC without the runtime, once (to try the Ready page, the permission prompt and
    the messages on a PC that has it; installing it again only repairs it). }
  VcForcedDone: Boolean;

function NeedsVcRuntime(): Boolean;
begin
  Result := VcRuntimeMissing() or ((ExpandConstant('{param:forcevc|0}') = '1') and not VcForcedDone);
end;

{ The setup log: Velopack's Setup writes it there (--log), kept after the wizard ends. }
function SetupLogPath(): String;
begin
  Result := AddBackslash(GetTempDir()) + '{#PackId}-setup.log';
end;

function HasAny(Text: String; Words: array of String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Words) - 1 do
    if Pos(Words[I], Text) > 0 then
    begin
      Result := True;
      Exit;
    end;
end;

{
  What to do about a failed install: a full drive or a Visual C++ runtime still missing (checked), else from the end of the
  log (or why Setup did not start): a file in use in the folder, a file Windows or an antivirus blocked; else try again.
}
function FailureCause(Started: Boolean; Tail: String): String;
var
  T: String;
begin
  T := Lowercase(Tail);
  if not HasRoom(ChosenDir(), {#NeedMB}) then
    Result := CustomMessage('CauseSpace')
  else if Started and HasAny(T, ['existing application directory', 'os error 32', 'used by another process']) then
    Result := CustomMessage('CauseInUse')
  else if not Started or HasAny(T, ['os error 5)', 'access is denied', 'os error 225', 'virus', 'os error 1260', 'blocked']) then
    Result := CustomMessage('CauseBlocked')
  else if VcRuntimeMissing() then
    Result := CustomMessage('CauseRuntime')
  else
    Result := CustomMessage('CauseOther');
end;

{ The failure in the user's language: what to do, the log's last lines and where it is (the box copies with Ctrl+C). }
procedure ShowFailure(Started: Boolean; Code: Integer; LogFile: String);
var
  Lines: TArrayOfString;
  Tail, Text: String;
  I, First, ErrorCode: Integer;
begin
  Tail := '';
  if LoadStringsFromFile(LogFile, Lines) then
  begin
    First := GetArrayLength(Lines) - 8;
    if First < 0 then
      First := 0;
    for I := First to GetArrayLength(Lines) - 1 do
      Tail := Tail + Copy(Lines[I], 1, 220) + #13#10;
  end;
  Text := FmtMessage(CustomMessage('InstallFailed'), [IntToStr(Code)]) + #13#10#13#10 + FailureCause(Started, Tail);
  if Tail <> '' then
    Text := Text + #13#10#13#10 + CustomMessage('LogTail') + #13#10 + Tail + #13#10
      + FmtMessage(CustomMessage('OpenLog'), [LogFile]);
  Log(Text);
  if Tail = '' then
    SuppressibleMsgBox(Text, mbError, MB_OK, IDOK)
  else if SuppressibleMsgBox(Text, mbError, MB_YESNO, IDNO) = IDYES then
    ShellExec('', LogFile, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

{ The Ready page says, only on a PC without it, that Microsoft Visual C++ is installed first (and why Windows asks). }
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
  if NeedsVcRuntime() then
    Result := Result + NewLine + NewLine + CustomMessage('VcReady');
end;

{
  Microsoft's runtime, before Kairos, only where it is missing: Windows asks for permission (it installs for the whole
  PC). '' when it is in; otherwise what to do. 3010 is "installed, restart later"; 1638 is "a newer one is there".
}
function InstallVcRuntime(): String;
var
  Code: Integer;
begin
  Result := '';
  WizardForm.StatusLabel.Caption := CustomMessage('InstallingRuntime');
  ExtractTemporaryFile('vc_redist.x64.exe');
  VcForcedDone := True;
  if not ShellExec('runas', ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE,
                   ewWaitUntilTerminated, Code) then
  begin
    Log('Visual C++: not started (' + IntToStr(Code) + ')');
    if Code = 1223 then { the permission prompt was refused }
      Result := CustomMessage('VcDeclined')
    else
      Result := FmtMessage(CustomMessage('VcFailed'), [IntToStr(Code)]);
    Exit;
  end;
  Log('Visual C++: exit code ' + IntToStr(Code));
  if not VcRuntimeMissing() then
    Exit;
  if Code = 1618 then { another install under way (often Windows Update) }
    Result := CustomMessage('VcBusy')
  else
    Result := FmtMessage(CustomMessage('VcFailed'), [IntToStr(Code)]);
end;

{ A Visual C++ problem: what to do, and Microsoft's page to open (never a bare error). }
procedure ShowVcProblem(Problem: String);
var
  ErrorCode: Integer;
begin
  Log(Problem);
  if SuppressibleMsgBox(Problem + #13#10#13#10 + CustomMessage('VcOpenPage'), mbError, MB_YESNO, IDNO) = IDYES then
    ShellExec('open', 'https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Started: Boolean;
  LogFile, Problem: String;
begin
  if CurStep = ssInstall then
  begin
    if NeedsVcRuntime() then
    begin
      Problem := InstallVcRuntime();
      if Problem <> '' then
      begin
        ShowVcProblem(Problem);
        Abort;
      end;
    end;
    WizardForm.StatusLabel.Caption := CustomMessage('InstallingApp');
    ExtractTemporaryFile('{#PackId}-win-Setup.exe');
    LogFile := SetupLogPath();
    DeleteFile(LogFile);
    Started := Exec(ExpandConstant('{tmp}\{#PackId}-win-Setup.exe'),
      '--silent --verbose --log "' + LogFile + '" --installto "' + ChosenDir() + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
    if not Started or (Code <> 0) then
    begin
      ShowFailure(Started, Code, LogFile);
      Abort;
    end;
    WriteChoices();
  end;
end;
