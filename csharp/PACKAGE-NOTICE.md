# Paketinhalt und Weitergabe

Dieses Verzeichnis ist ein eigenständiges Windows-x64-Paket. Es braucht weder Python noch
eine separat installierte .NET-Laufzeit oder FFmpeg.

| Bestandteil | Herkunft | Lizenz |
| --- | --- | --- |
| Anwendung und .NET-Laufzeit | dieses Projekt, self-contained veröffentlicht | Projektlizenz, .NET unter MIT |
| OCR-Modelle PP-OCRv5 und Zeichensatz | RapidOcrNet 4.2.0, unverändert | Apache-2.0, siehe `licenses/` |
| OpenCvSharp und native OpenCV-Dateien | NuGet | Apache-2.0 |
| ONNX Runtime | NuGet | MIT |
| LibVLC und LibVLCSharp (nur Desktop, Videovorschau) | NuGet | LGPL-2.1-or-later, siehe `THIRD-PARTY-NOTICES.md` |
| `tools/ffmpeg.exe`, `tools/ffprobe.exe` | beim Paketieren aus einer vorhandenen Installation kopiert | siehe unten |

## FFmpeg: Buildoptionen entscheiden über die Weitergabe

FFmpeg wird nicht in diesem Repository mitgeliefert, sondern beim Paketieren kopiert. Die
genauen Buildoptionen der kopierten Dateien stehen in `licenses/FFMPEG-BUILD.txt`.

- Ein Build mit `--enable-gpl` (üblich bei den verbreiteten Windows-Builds, enthält x264
  und x265) steht unter der **GPL**. Wer ein solches Paket an andere weitergibt, muss die
  Bedingungen der GPL erfüllen, einschließlich des Angebots der Quelltexte für FFmpeg.
- Ein Build ohne `--enable-gpl` steht unter der **LGPL**. Dann genügt es, FFmpeg als
  ersetzbare Programmdatei beizulegen und die Lizenz zu nennen.

Für die eigene Nutzung auf dem eigenen Rechner ist beides unproblematisch. Vor einer
Weitergabe des Pakets ist die Entscheidung bewusst zu treffen; der Standardweg dieses
Skripts kopiert den vorhandenen Build ungeprüft.

Der verwendete Encoder ist einstellbar (`clips.video_codec`). Mit `h264_nvenc` wird die
NVIDIA-Hardware verwendet, sofern der Build sie unterstützt.

## Erststart

```powershell
.\bf6-highlights.exe config-import config.example.yaml config.yaml
.\bf6-highlights.exe analyze "D:\Aufnahmen\match.mkv" config.yaml "D:\Aufnahmen\Highlights"
```

Die Oberfläche startet mit `Desktop.exe`. Konfiguration, Berichte und Clips werden dorthin
geschrieben, wo sie angegeben werden; das Installationsverzeichnis muss nicht beschreibbar
sein. Es gibt keine Netzwerkverbindung, keinen Download und keine Telemetrie.
