package com.agentgrid.mobile

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.media.SoundPool
import android.os.VibrationEffect
import android.os.Vibrator
import android.util.Log
import java.util.concurrent.Executors

enum class SoundTheme(val label: String) {
    RIMWORLD("环世界"),
    ARCADE("复古街机");

    companion object {
        fun fromName(name: String?): SoundTheme =
            entries.firstOrNull { it.name.equals(name, ignoreCase = true) } ?: RIMWORLD
    }
}

class SoundEngine(private val context: Context) {
    private val executor = Executors.newSingleThreadExecutor()
    private val vibrator = context.getSystemService(Vibrator::class.java)
    private val audioManager = context.getSystemService(AudioManager::class.java)
    private val preferences = context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
    private val focusRequest = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_MAY_DUCK)
        .setAudioAttributes(audioAttributes)
        .setOnAudioFocusChangeListener { }
        .build()

    private val soundPool: SoundPool by lazy {
        SoundPool.Builder()
            .setMaxStreams(5)
            .setAudioAttributes(audioAttributes)
            .build()
            .apply {
                setOnLoadCompleteListener { _, sampleId, status ->
                    if (status == 0) {
                        loadedSamples.add(sampleId)
                        Log.i(LOG_TAG, "RimWorld 音效加载成功: sampleId=$sampleId")
                    } else {
                        Log.e(LOG_TAG, "RimWorld 音效加载失败: sampleId=$sampleId, status=$status")
                    }
                }
            }
    }

    private val loadedSamples = java.util.Collections.synchronizedSet(mutableSetOf<Int>())
    private var rimGoodId = 0
    private var rimWarningId = 0
    private var rimErrorId = 0
    private var rimNeutralId = 0
    private var rimClickId = 0

    init {
        runCatching {
            rimGoodId = soundPool.load(context, R.raw.sound_rim_letter_good, 1)
            rimWarningId = soundPool.load(context, R.raw.sound_rim_letter_warning, 1)
            // 任务出错失败：采用最高紧迫度 BadUrgent.wav（停用海盗突袭 BadUrgentBig）
            rimErrorId = soundPool.load(context, R.raw.sound_rim_letter_raid, 1)
            rimNeutralId = soundPool.load(context, R.raw.sound_rim_letter_neutral, 1)
            rimClickId = soundPool.load(context, R.raw.sound_rim_click, 1)
            Log.i(LOG_TAG, "RimWorld 音效请求加载: click=$rimClickId good=$rimGoodId neutral=$rimNeutralId error=$rimErrorId warning=$rimWarningId")
        }.onFailure { Log.e(LOG_TAG, "加载 RimWorld 原生音效失败", it) }
    }

    val isEnabled: Boolean
        get() = preferences.getBoolean(SOUND_ENABLED_KEY, true)

    fun setEnabled(enabled: Boolean) {
        preferences.edit().putBoolean(SOUND_ENABLED_KEY, enabled).apply()
    }

    val soundTheme: SoundTheme
        get() = SoundTheme.fromName(preferences.getString(SOUND_THEME_KEY, SoundTheme.RIMWORLD.name))

    fun setSoundTheme(theme: SoundTheme) {
        preferences.edit().putString(SOUND_THEME_KEY, theme.name).apply()
    }

    // 震动单独开关（跟提示音解绑）：开会/静音场景只留震动，或全关。
    val isVibrationEnabled: Boolean
        get() = preferences.getBoolean(VIBRATION_ENABLED_KEY, true)

    fun setVibrationEnabled(enabled: Boolean) {
        preferences.edit().putBoolean(VIBRATION_ENABLED_KEY, enabled).apply()
    }

    fun play(cue: ArcadeSoundCue) {
        if (!isEnabled) return
        executor.execute {
            runCatching {
                if (soundTheme == SoundTheme.RIMWORLD) {
                    playRimCue(cue)
                } else {
                    playArcadeCue(cue)
                }
            }.onFailure { Log.e(LOG_TAG, "提示音播放失败：$cue", it) }
        }
        // 任务完成 + 等输入（question/审批）都震；崩溃也震。
        if (isVibrationEnabled) {
            when (cue) {
                ArcadeSoundCue.TASK_INTERRUPTED -> runCatching {
                    vibrator?.vibrate(VibrationEffect.createOneShot(180, 150))
                }
                ArcadeSoundCue.INPUT_REQUIRED -> runCatching {
                    vibrator?.vibrate(VibrationEffect.createOneShot(80, 110))
                }
                ArcadeSoundCue.TASK_COMPLETE -> runCatching {
                    vibrator?.vibrate(VibrationEffect.createOneShot(60, 90))
                }
                ArcadeSoundCue.TASK_ACKNOWLEDGE -> Unit
            }
        }
    }

    fun playClick() {
        if (!isEnabled || soundTheme != SoundTheme.RIMWORLD || rimClickId <= 0) return
        val streamId = soundPool.play(rimClickId, 1f, 1f, 0, 0, 1f)
        if (streamId == 0) {
            Log.w(LOG_TAG, "playClick 播放返回 0 (可能尚未解码完成): rimClickId=$rimClickId")
        }
        if (isVibrationEnabled) {
            runCatching {
                vibrator?.vibrate(VibrationEffect.createOneShot(15, 60))
            }
        }
    }

    fun close() {
        executor.shutdownNow()
        runCatching { soundPool.release() }
    }

    private fun playRimCue(cue: ArcadeSoundCue) {
        val soundId = when (cue) {
            ArcadeSoundCue.TASK_COMPLETE -> rimGoodId
            ArcadeSoundCue.INPUT_REQUIRED -> rimWarningId
            ArcadeSoundCue.TASK_INTERRUPTED -> rimErrorId
            ArcadeSoundCue.TASK_ACKNOWLEDGE -> rimNeutralId
        }
        if (soundId > 0) {
            val focusResult = audioManager?.requestAudioFocus(focusRequest)
            val streamId = soundPool.play(soundId, 1f, 1f, 1, 0, 1f)
            Log.i(LOG_TAG, "playRimCue: cue=$cue soundId=$soundId streamId=$streamId focus=$focusResult")
        }
    }

    private fun playArcadeCue(cue: ArcadeSoundCue) {
        val sampleRate = AudioTrack.getNativeOutputSampleRate(AudioManager.STREAM_MUSIC)
            .takeIf { it >= 8_000 }
            ?: ArcadeSoundSynthesizer.DEFAULT_SAMPLE_RATE
        val samples = ArcadeSoundSynthesizer.render(cue, sampleRate)
        val bufferSize = maxOf(
            samples.size * Short.SIZE_BYTES,
            AudioTrack.getMinBufferSize(
                sampleRate,
                AudioFormat.CHANNEL_OUT_MONO,
                AudioFormat.ENCODING_PCM_16BIT,
            ),
        )
        val track = AudioTrack.Builder()
            .setAudioAttributes(audioAttributes)
            .setAudioFormat(
                AudioFormat.Builder()
                    .setSampleRate(sampleRate)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .build(),
            )
            .setTransferMode(AudioTrack.MODE_STATIC)
            .setBufferSizeInBytes(bufferSize)
            .build()
        try {
            check(AudioTrackStatePolicy.canWrite(AudioTrack.MODE_STATIC, track.state)) {
                "AudioTrack 无法写入：state=${track.state}"
            }
            val focusResult = audioManager?.requestAudioFocus(focusRequest)
            val writtenSamples = track.write(samples, 0, samples.size)
            check(writtenSamples == samples.size) {
                "PCM 写入不完整：$writtenSamples/${samples.size}"
            }
            check(track.state == AudioTrack.STATE_INITIALIZED) {
                "PCM 写入后 AudioTrack 未就绪：state=${track.state}"
            }
            track.setVolume(1f)
            track.play()
            Thread.sleep(samples.size * 1_000L / sampleRate + 40L)
            Log.i(
                LOG_TAG,
                "提示音播放完成：cue=$cue focus=$focusResult " +
                    "frames=${track.playbackHeadPosition}/${samples.size}",
            )
        } finally {
            runCatching { track.stop() }
            track.release()
            audioManager?.abandonAudioFocusRequest(focusRequest)
        }
    }

    companion object {
        private const val LOG_TAG = "AgentPagerSound"
        private const val PREFERENCES_NAME = "agentgrid_sound"
        private const val SOUND_ENABLED_KEY = "enabled"
        private const val SOUND_THEME_KEY = "sound_theme"
        private const val VIBRATION_ENABLED_KEY = "vibration_enabled"

        private val audioAttributes = AudioAttributes.Builder()
            // 终端是常亮前台界面，走媒体通道，避免被手机的通知静音模式吞掉。
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
            .build()
    }
}

internal object AudioTrackStatePolicy {
    fun canWrite(transferMode: Int, state: Int): Boolean = when (transferMode) {
        AudioTrack.MODE_STATIC ->
            state == AudioTrack.STATE_NO_STATIC_DATA ||
                state == AudioTrack.STATE_INITIALIZED

        AudioTrack.MODE_STREAM -> state == AudioTrack.STATE_INITIALIZED
        else -> false
    }
}
