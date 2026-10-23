# Security

## Reporting a vulnerability

Please don't open a public issue for a security problem. Report it privately on GitHub instead: on this repository, open **Security**, then **Report a vulnerability**. Only the maintainer sees it.

Tell us what you found, how to reproduce it, and which Kairos version you used (Settings → General, at the foot of the page). We reply as soon as we can and credit you in the release notes if you like.

## Supported versions

Only the latest release gets security fixes. Kairos updates itself from inside the app, so the fix reaches you there.

## Getting Kairos safely

- Download Kairos only from [the releases page](https://github.com/BasilForge/Kairos/releases).
- Each release lists the SHA-256 of its files. To check a download, run this in PowerShell and compare the result with the release page:

  ```powershell
  Get-FileHash .\Kairos-Setup.exe
  ```

- Updates come from the same releases page over HTTPS, and Velopack checks each update against the checksum the release lists before putting it in.
- The auto captions engine and model are downloaded only after you agree, and Kairos checks their SHA-256 before using them.

## What Kairos does with your data

See [Privacy](README.md#privacy) in the README: what Kairos keeps on your PC, and the only times it goes online.

## Libraries

Kairos is built on libobs (OBS Studio), FFmpeg, ONNX Runtime, whisper.cpp, Velopack and Microsoft Edge WebView2. A problem in one of them is best reported to that project too; Kairos ships their fixes in its next release.

---

## Sécurité

### Signaler une vulnérabilité

Merci de ne pas ouvrir d’issue publique pour un problème de sécurité. Signalez-le en privé sur GitHub : dans ce dépôt, ouvrez **Security**, puis **Report a vulnerability**. Seul le mainteneur voit le signalement.

Indiquez ce que vous avez trouvé, comment reproduire le problème et la version de Kairos utilisée (**Paramètres → Général**, en bas de la page). Nous vous répondrons dès que possible et pourrons vous citer dans les notes de version si vous le souhaitez.

### Versions prises en charge

Seule la dernière version reçoit les correctifs de sécurité. Kairos se met à jour depuis l’application pour vous permettre de les recevoir.

### Télécharger Kairos en toute sécurité

- Téléchargez Kairos uniquement depuis la [page des versions](https://github.com/BasilForge/Kairos/releases).
- Chaque version indique l’empreinte SHA-256 de ses fichiers. Pour vérifier un téléchargement, lancez cette commande dans PowerShell et comparez le résultat à la page de la version :

  ```powershell
  Get-FileHash .\Kairos-Setup.exe
  ```

- Les mises à jour proviennent de la même page des versions, via HTTPS. Velopack vérifie chaque mise à jour à l’aide de l’empreinte indiquée avant de l’appliquer.
- Le moteur et le modèle de sous-titrage automatique sont téléchargés uniquement avec votre accord. Kairos vérifie leur empreinte SHA-256 avant de les utiliser.

### Vos données

Consultez la section [Confidentialité du README français](docs/README.fr.md#confidentialité) pour savoir ce que Kairos conserve sur votre PC et dans quels cas il se connecte à Internet.

### Bibliothèques

Kairos utilise libobs (OBS Studio), FFmpeg, ONNX Runtime, whisper.cpp, Velopack et Microsoft Edge WebView2. Un problème concernant l’un de ces composants doit de préférence aussi être signalé au projet concerné ; Kairos inclut ses correctifs dans sa prochaine version.

---

## الإبلاغ عن ثغرة أمنية

لا تفتح بلاغًا عامًا عن مشكلة أمنية. أبلغ عنها بشكل خاص على GitHub: افتح **Security** في هذا المستودع، ثم **Report a vulnerability**. لا يراها إلا المشرف على المشروع.

اذكر ما وجدته، وكيف يمكن تكرار المشكلة، ورقم إصدار كايروس الذي استخدمته. لا تصل الإصلاحات الأمنية إلا إلى أحدث إصدار، ويحدّث كايروس نفسه من داخل التطبيق.

نزّل كايروس من [صفحة الإصدارات](https://github.com/BasilForge/Kairos/releases) فقط. تذكر كل نسخة بصمة SHA-256 لملفاتها، وتستطيع مطابقتها بالأمر `Get-FileHash .\Kairos-Setup.exe` في PowerShell.
