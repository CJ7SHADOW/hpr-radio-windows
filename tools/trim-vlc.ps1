# Keeps only the parts of VLC an internet-radio player needs (run after dotnet publish).
#   pwsh tools/trim-vlc.ps1 publish
#
# The VLC package ships 32-bit, 64-bit and ARM copies with every plugin (video, subtitles,
# streaming out...). This app is 64-bit and audio-only. The self-test logs which modules are
# actually used (HPR_VLC_MODULES=1): https access + GnuTLS, the "es" demuxer, mpg123, the
# mpegaudio packetizer, prefetch/record stream filters, float mixer, ugly resampler, scaletempo.
# The list below keeps those plus close relatives (AAC/Ogg/Opus/FLAC, playlists, HLS) so a
# stream change at HPR doesn't silently stop playback, and every audio output Windows might use.
param([string]$Publish = "publish")
$ErrorActionPreference = "Stop"
$vlc = Join-Path $Publish "libvlc"
if (-not (Test-Path $vlc)) { throw "No libvlc folder in $Publish" }

# Other architectures: not used by a 64-bit app.
Get-ChildItem $vlc -Directory | Where-Object Name -ne "win-x64" | Remove-Item -Recurse -Force

$root = Join-Path $vlc "win-x64"
$keep = @(
  "libvlc.dll", "libvlccore.dll",
  # how audio gets in
  "plugins\access\libhttp_plugin.dll", "plugins\access\libhttps_plugin.dll",
  "plugins\access\libfilesystem_plugin.dll", "plugins\access\libtcp_plugin.dll",
  "plugins\misc\libgnutls_plugin.dll",
  "plugins\keystore\*", "plugins\logger\*", "plugins\stream_filter\*",
  # containers and playlists
  "plugins\demux\libes_plugin.dll", "plugins\demux\libmp4_plugin.dll", "plugins\demux\libogg_plugin.dll",
  "plugins\demux\libwav_plugin.dll", "plugins\demux\libplaylist_plugin.dll", "plugins\demux\libadaptive_plugin.dll",
  "plugins\demux\libts_plugin.dll", "plugins\demux\libflacsys_plugin.dll",
  # decoders: MP3, AAC, Ogg Vorbis, Opus, FLAC, PCM
  "plugins\codec\libmpg123_plugin.dll", "plugins\codec\libmpeg_audio_plugin.dll", "plugins\codec\libfaad_plugin.dll",
  "plugins\codec\libvorbis_plugin.dll", "plugins\codec\libopus_plugin.dll", "plugins\codec\libflac_plugin.dll",
  "plugins\codec\libaraw_plugin.dll",
  "plugins\packetizer\*",
  # how audio gets out
  "plugins\audio_output\*", "plugins\audio_filter\*", "plugins\audio_mixer\*"
)

$wanted = @{}
foreach ($pattern in $keep) {
  Get-ChildItem (Join-Path $root $pattern) -File -ErrorAction SilentlyContinue | ForEach-Object { $wanted[$_.FullName] = $true }
}
$before = (Get-ChildItem $root -Recurse -File | Measure-Object Length -Sum).Sum
Get-ChildItem $root -Recurse -File | Where-Object { -not $wanted.ContainsKey($_.FullName) } | Remove-Item -Force
# Drop emptied folders; plugins.dat was removed too, so VLC rescans the few plugins left.
Get-ChildItem $root -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending |
  Where-Object { -not (Get-ChildItem $_.FullName -Force) } | Remove-Item -Force
$after = (Get-ChildItem $root -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("VLC trimmed: {0:N0} MB -> {1:N0} MB, {2} files kept" -f ($before/1MB), ($after/1MB), (Get-ChildItem $root -Recurse -File).Count)
foreach ($must in "libvlc.dll", "libvlccore.dll", "plugins\codec\libmpg123_plugin.dll", "plugins\access\libhttps_plugin.dll", "plugins\misc\libgnutls_plugin.dll", "plugins\audio_output\libmmdevice_plugin.dll") {
  if (-not (Test-Path (Join-Path $root $must))) { throw "Trimmed away something essential: $must" }
}
