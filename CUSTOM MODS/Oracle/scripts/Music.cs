using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CeZeY.OracleScript;

public partial class Oracle
{
    private const string MusicFolder = "Music";
    private const string MusicAlias = "oraclemusic";
    private const string MusicThreadKey = "CeZeY.Oracle.MusicThread";

    private static readonly string[] MusicExtensions = { ".mp3", ".wav", ".wma" };

    private static readonly List<string> _tracks = new();
    private static int _track = -1;
    private static bool _musicPlaying;
    private static bool _musicPausedByGame;
    private static Feature? _trackPick;
    private static readonly Random _shuffle = new();
    private static MusicWorker? _music;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, nint hwndCallback);

    private sealed class MusicWorker
    {
        private readonly BlockingCollection<Action> _jobs = new();
        private readonly Thread _thread;
        private volatile bool _open;
        public volatile bool Finished;
        public volatile string Error = "";

        public MusicWorker()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Oracle music" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void Post(Action job) => _jobs.Add(job);

        public void Stop() => _jobs.CompleteAdding();

        private static string Send(string cmd)
        {
            var sb = new StringBuilder(128);
            int err = mciSendString(cmd, sb, sb.Capacity, 0);
            return err == 0 ? sb.ToString() : "!" + err;
        }

        public void Open(string file, int volume)
        {
            Close();
            string type = file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "waveaudio" : "mpegvideo";
            string r = Send("open \"" + file + "\" type " + type + " alias " + MusicAlias);
            if (r.StartsWith("!") && type == "waveaudio")
                r = Send("open \"" + file + "\" type mpegvideo alias " + MusicAlias);
            if (r.StartsWith("!"))
            {
                Error = "could not open " + Path.GetFileName(file) + " (MCI error " + r.Substring(1) + ")";
                return;
            }

            _open = true;
            Finished = false;
            SetVolume(volume);
            Send("play " + MusicAlias);
        }

        public void SetVolume(int volume)
        {
            if (_open)
                Send("setaudio " + MusicAlias + " volume to " + Math.Clamp(volume, 0, 1000));
        }

        public void Pause()
        {
            if (_open)
                Send("pause " + MusicAlias);
        }

        public void Resume()
        {
            if (_open)
                Send("resume " + MusicAlias);
        }

        public void Close()
        {
            if (!_open)
                return;
            Send("stop " + MusicAlias);
            Send("close " + MusicAlias);
            _open = false;
        }

        private void Loop()
        {
            while (!_jobs.IsCompleted)
            {
                if (_jobs.TryTake(out Action? job, 250))
                {
                    try
                    {
                        job();
                    }
                    catch (Exception ex)
                    {
                        Error = ex.Message;
                    }
                }

                if (_open && Send("status " + MusicAlias + " mode") == "stopped")
                {
                    Close();
                    Finished = true;
                }
            }

            Close();
        }
    }

    private static string MusicDir => Path.Combine(_modDir, MusicFolder);

    private static int MusicVolume => (int)MathF.Round((FeatureById.TryGetValue("music_volume", out Feature? f) ? f.Value : 0.7f) * 1000f);

    private static int MusicOrder => FeatureById.TryGetValue("music_order", out Feature? f) ? f.Choice : 0;

    private static bool MusicFollowsPause => !FeatureById.TryGetValue("music_pause_with_game", out Feature? f) || f.On;

    private static void BuildMusic()
    {
        _trackPick = Choice("music_track", "TRACK", "Songs in BmGame\\Mods\\Oracle\\Music (mp3, wav or wma). Pick one to play it.",
            new[] { "none" }, new[] { "NO SONGS FOUND" }, PlayPicked);
        _trackPick.Pref = true;
        Action("music_play", "PLAY / PAUSE", "Starts the music, or pauses and resumes it.", MusicPlayPause);
        Action("music_next", "NEXT TRACK", "Skips to the next song.", () => MusicStep(1));
        Action("music_prev", "PREVIOUS TRACK", "Goes back to the previous song.", () => MusicStep(-1));
        Action("music_stop", "STOP", "Stops the music.", MusicStop);
        Feature vol = Slider("music_volume", "VOLUME", "Volume of your music. The game's own music volume is in the game options.", 0.7f, 0f, 1f, 0.05f, 0.35f, Percent,
            _ => _music?.Post(() => _music.SetVolume(MusicVolume)));
        vol.Pref = true;
        Feature order = Choice("music_order", "ORDER", "IN ORDER plays the folder top to bottom, SHUFFLE picks at random, REPEAT ONE loops the current song.",
            new[] { "in_order", "shuffle", "repeat_one" }, new[] { "IN ORDER", "SHUFFLE", "REPEAT ONE" }, _ => { });
        order.Pref = true;
        Feature follow = Toggle("music_pause_with_game", "PAUSE WITH GAME", "Pauses your music while the game's pause menu is open.", _ => { });
        follow.Pref = true;
        follow.Default = 1f;
        follow.On = true;
        Action("music_rescan", "RELOAD SONG LIST", "Reads the Music folder again after you add or remove songs.", () =>
        {
            ScanMusic();
            ShowNotice("MUSIC", _tracks.Count + (_tracks.Count == 1 ? " SONG" : " SONGS"), _tracks.Count > 0);
        });
        Action("music_folder", "OPEN MUSIC FOLDER", "Opens the Music folder in Windows Explorer so you can drop songs in.", () =>
        {
            Directory.CreateDirectory(MusicDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + MusicDir + "\"") { UseShellExecute = true });
        });
    }

    private static void InitMusic()
    {
        try
        {
            Directory.CreateDirectory(MusicDir);
            string readme = Path.Combine(MusicDir, "PUT YOUR SONGS HERE.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme, "Drop .mp3, .wav or .wma files in this folder, then open the Oracle menu (F3) > MUSIC.\r\nSongs play in alphabetical order; rename them with numbers (01 ..., 02 ...) to choose the order.\r\n");
        }
        catch (Exception ex)
        {
            Log("Music folder: " + ex.Message);
        }

        if (AppDomain.CurrentDomain.GetData(MusicThreadKey) is MusicWorker old)
            old.Stop();
        _music = new MusicWorker();
        AppDomain.CurrentDomain.SetData(MusicThreadKey, _music);
        ScanMusic();
    }

    private static void ScanMusic()
    {
        string current = _track >= 0 && _track < _tracks.Count ? _tracks[_track] : "";
        _tracks.Clear();
        try
        {
            if (Directory.Exists(MusicDir))
            {
                foreach (string f in Directory.GetFiles(MusicDir))
                {
                    if (Array.IndexOf(MusicExtensions, Path.GetExtension(f).ToLowerInvariant()) >= 0)
                        _tracks.Add(f);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Music scan failed: " + ex.Message);
        }

        _tracks.Sort(StringComparer.OrdinalIgnoreCase);
        _track = current.Length > 0 ? _tracks.IndexOf(current) : -1;

        if (_trackPick == null)
            return;

        if (_tracks.Count == 0)
        {
            _trackPick.ChoiceKeys = new[] { "none" };
            _trackPick.Choices = new[] { "NO SONGS FOUND" };
            _trackPick.Choice = 0;
            return;
        }

        var keys = new string[_tracks.Count];
        var labels = new string[_tracks.Count];
        for (int i = 0; i < _tracks.Count; i++)
        {
            keys[i] = Path.GetFileName(_tracks[i]);
            labels[i] = TrackLabel(_tracks[i]);
        }

        _trackPick.ChoiceKeys = keys;
        _trackPick.Choices = labels;
        _trackPick.Choice = Math.Max(0, _track);
    }

    private static string TrackLabel(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file).Replace('_', ' ').ToUpperInvariant();
        return name.Length > 30 ? name.Substring(0, 29) + "…" : name;
    }

    private static void PlayPicked(int index)
    {
        if (_tracks.Count == 0)
            return;
        PlayTrack(Math.Clamp(index, 0, _tracks.Count - 1));
    }

    private static void PlayTrack(int index)
    {
        if (_music == null || _tracks.Count == 0)
            return;

        _track = (index % _tracks.Count + _tracks.Count) % _tracks.Count;
        string file = _tracks[_track];
        int volume = MusicVolume;
        _music.Error = "";
        _music.Post(() => _music.Open(file, volume));
        _musicPlaying = true;
        _musicPausedByGame = false;
        if (_trackPick != null)
            _trackPick.Choice = _track;
        ShowNotice("NOW PLAYING", TrackLabel(file), true);
        Log("Music: " + Path.GetFileName(file));
    }

    private static void MusicPlayPause()
    {
        if (_tracks.Count == 0)
            ScanMusic();
        if (_tracks.Count == 0)
        {
            ShowNotice("MUSIC", "NO SONGS IN THE MUSIC FOLDER", false);
            return;
        }

        if (_track < 0)
        {
            PlayTrack(MusicOrder == 1 ? _shuffle.Next(_tracks.Count) : 0);
            return;
        }

        if (_musicPlaying)
        {
            _music?.Post(_music.Pause);
            _musicPlaying = false;
            ShowNotice("MUSIC", "PAUSED", false);
        }
        else
        {
            _music?.Post(_music.Resume);
            _musicPlaying = true;
            ShowNotice("MUSIC", "PLAYING", true);
        }
    }

    private static void MusicStep(int dir)
    {
        if (_tracks.Count == 0)
            ScanMusic();
        if (_tracks.Count == 0)
        {
            ShowNotice("MUSIC", "NO SONGS IN THE MUSIC FOLDER", false);
            return;
        }

        if (MusicOrder == 1 && _tracks.Count > 1)
        {
            int next;
            do
                next = _shuffle.Next(_tracks.Count);
            while (next == _track);
            PlayTrack(next);
            return;
        }

        PlayTrack(_track < 0 ? 0 : _track + dir);
    }

    private static void MusicStop()
    {
        _music?.Post(_music.Close);
        _musicPlaying = false;
        _musicPausedByGame = false;
        _track = -1;
    }

    private static void MusicFrame(bool paused)
    {
        if (_music == null)
            return;

        if (_music.Error.Length > 0)
        {
            Log("Music: " + _music.Error);
            ShowNotice("MUSIC", "CAN'T PLAY THIS FILE", false);
            _music.Error = "";
            _musicPlaying = false;
        }

        if (_musicPlaying && MusicFollowsPause)
        {
            if (paused && !_musicPausedByGame)
            {
                _musicPausedByGame = true;
                _music.Post(_music.Pause);
            }
            else if (!paused && _musicPausedByGame)
            {
                _musicPausedByGame = false;
                _music.Post(_music.Resume);
            }
        }

        if (_musicPlaying && _music.Finished)
        {
            _music.Finished = false;
            if (MusicOrder == 2)
                PlayTrack(_track);
            else
                MusicStep(1);
        }
    }
}
