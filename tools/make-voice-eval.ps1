# Windows PowerShell 5.1 (System.Speech): Smart Save's evaluation set, made silently to WAV files (16 kHz mono).
param([string]$Out = $PSScriptRoot)
Add-Type -AssemblyName System.Speech
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)

function Say([string]$voice, [string]$file, [string]$ssmlBody, [int]$rate = 0) {
    $s = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $s.SelectVoice($voice)
    $s.Rate = $rate
    $s.SetOutputToWaveFile((Join-Path $Out $file), $format)
    $ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>$ssmlBody</speak>"
    $s.SpeakSsml($ssml)
    $s.Dispose()
}

$phrase = 'Kairos, clip that.'
$chat = @(
    'Push B, they are all rotating.', 'I need ammo, anyone got light rounds?', 'That clip was crazy, did you see it?',
    'Clip that guy, he is cheating for sure.', 'Okay, I am going in, cover me.', 'Wait, wait, there is one more on the roof.',
    'Kai rose up the ladder and got the kill.', 'Close that door behind you.', 'Can you clip it later, I am busy?',
    'Nice shot, that was clean.', 'Chaos everywhere, I cannot see anything.', 'Cairo is a cool map honestly.',
    'I think we should rotate to the left side.', 'Who has the bomb right now?', 'My internet is lagging so bad today.',
    'Let me heal first, then we push.', 'He is one shot, finish him.', 'That was a close one, I almost died.',
    'Do you want to queue again after this?', 'I clipped that earlier, it was so funny.', 'Keep it, keep it, do not reload.',
    'Car is coming from the right, watch out.', 'Clipped and saved, nice.', 'Kairos is the app I use for clips.',
    'Flank them from the back, quick.', 'I am out of shields, give me a cell.', 'Let us go, we won the round.',
    'Hold on, my mic is too loud.', 'They are camping in the building, use the grenade.', 'Good game everyone, well played.'
)

foreach ($v in @(@('Microsoft David Desktop', 'david'), @('Microsoft Zira Desktop', 'zira'))) {
    $voice = $v[0]; $tag = $v[1]
    foreach ($r in -2..2) { Say $voice "$tag-phrase-r$($r + 2).wav" $phrase $r }
    # Talk with natural and varied pauses between sentences (0.2 to 0.9 s): every start after a breath is checked.
    $body = ($chat | ForEach-Object -Begin { $i = 0 } -Process { $pause = @(200, 300, 450, 600, 900)[$i++ % 5]; "$_ <break time='$($pause)ms'/>" }) -join ' '
    Say $voice "$tag-talk.wav" $body 0
    # The phrase in talk: said first then talk at once; after a breath; inside a sentence with no breath.
    Say $voice "$tag-first-then-talk.wav" "$phrase did you see that, he was flying across the map, insane." 0
    Say $voice "$tag-after-breath.wav" "Okay guys <break time='350ms'/> $phrase that was the best play of the night." 0
    Say $voice "$tag-after-long-breath.wav" "Wow <break time='700ms'/> $phrase I cannot believe that worked." 0
    Say $voice "$tag-inside-sentence.wav" "I swear if you say Kairos clip that one more time I am leaving." 0
}
Get-ChildItem $Out -Filter *.wav | Select-Object Name, @{n = 's'; e = { [math]::Round(($_.Length - 44) / 32000, 1) } } | Format-Table -AutoSize | Out-String
