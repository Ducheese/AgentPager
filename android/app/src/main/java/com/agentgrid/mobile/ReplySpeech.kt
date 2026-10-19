package com.agentgrid.mobile

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioManager
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import android.util.Log
import java.util.Locale
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * REPLY 朗读（系统 TTS）。进程级单例：不绑定 Activity/ViewModel，
 * 界面退后台、Activity 被回收都不打断播放；配合前台保活服务，进程不被冻结即可续读。
 * 停止只有三条路：再点一次 REPLY 标签、通知栏「停止朗读」、解除配对。
 *
 * 失败不再静默：找不到引擎/缺中文数据/引擎报错都会通过 [failure] 交给界面显示一行红字。
 * 语速来自设置（默认 2.0x）；朗读期间申请 MAY_DUCK 音频焦点，结束或被打断即释放。
 */
object ReplySpeech {
    data class SpeechFailure(val taskID: String, val message: String)

    private var tts: TextToSpeech? = null
    private var engineReady = false
    private var pendingSpeech: (() -> Unit)? = null
    private var pendingTaskID: String? = null
    private var currentTaskID: String? = null
    /** 每轮朗读一个自增会话号：utterance id 全局唯一，上一轮的迟到回调不会误清新一轮状态。 */
    private var session = 0L
    private var lastUtteranceId: String? = null

    private var audioManager: AudioManager? = null
    private var focusRequest: AudioFocusRequest? = null
    private var focusHeld = false

    private val mutableSpeakingTaskId = MutableStateFlow<String?>(null)
    val speakingTaskId: StateFlow<String?> = mutableSpeakingTaskId.asStateFlow()

    private val mutableFailure = MutableStateFlow<SpeechFailure?>(null)
    val failure: StateFlow<SpeechFailure?> = mutableFailure.asStateFlow()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var failureJob: Job? = null

    fun toggle(context: Context, taskId: String, markdown: String) {
        if (mutableSpeakingTaskId.value == taskId) {
            stop()
        } else {
            speak(context, taskId, markdown)
        }
    }

    fun speak(context: Context, taskId: String, markdown: String) {
        val text = ReplySpeechText.speakable(markdown)
        if (text.isBlank()) {
            stop()
            return
        }
        val chunks = ReplySpeechText.chunk(text)
        clearFailure()
        // 同一时刻只有一个意图：初始化未完成时再点别的任务，以最后一次点击为准
        pendingTaskID = taskId
        mutableSpeakingTaskId.value = taskId
        ensureEngine(context.applicationContext) { startUtterances(context.applicationContext, chunks) }
    }

    fun stop() {
        mutableSpeakingTaskId.value = null
        pendingSpeech = null
        pendingTaskID = null
        currentTaskID = null
        lastUtteranceId = null
        runCatching { tts?.stop() }
        abandonFocus()
    }

    private fun ensureEngine(context: Context, onReady: () -> Unit) {
        if (tts != null && engineReady) {
            onReady()
            return
        }
        pendingSpeech = onReady
        if (tts != null) return
        tts = TextToSpeech(context.applicationContext) { status ->
            engineReady = status == TextToSpeech.SUCCESS
            if (!engineReady) {
                report(pendingTaskID, "朗读不可用：找不到语音引擎")
                Log.e(LOG_TAG, "TTS 初始化失败 status=$status")
                runCatching { tts?.shutdown() }
                tts = null
                pendingSpeech = null
                pendingTaskID = null
                mutableSpeakingTaskId.value = null
                return@TextToSpeech
            }
            val language = tts?.setLanguage(Locale.SIMPLIFIED_CHINESE)
            val usable = language != TextToSpeech.LANG_MISSING_DATA &&
                language != TextToSpeech.LANG_NOT_SUPPORTED
            if (!usable) {
                val fallback = tts?.setLanguage(Locale.CHINA)
                if (fallback == TextToSpeech.LANG_MISSING_DATA ||
                    fallback == TextToSpeech.LANG_NOT_SUPPORTED
                ) {
                    report(pendingTaskID, "朗读不可用：缺少中文语音数据")
                    runCatching { tts?.shutdown() }
                    tts = null
                    engineReady = false
                    pendingSpeech = null
                    pendingTaskID = null
                    mutableSpeakingTaskId.value = null
                    return@TextToSpeech
                }
            }
            tts?.setPitch(SPEECH_PITCH)
            val pending = pendingSpeech
            pendingSpeech = null
            pending?.invoke()
        }
    }

    private fun startUtterances(context: Context, chunks: List<String>) {
        val engine = tts ?: return
        currentTaskID = pendingTaskID
        // 每次开读都按设置重设：同一进程里改语速不必重建引擎
        engine.setSpeechRate(rateOf(context))
        requestFocus(context)
        // 会话号先自增再下发：QUEUE_FLUSH 打断旧句时旧 id 前缀已是上一轮，天然不会被误清
        val currentSession = ++session
        val idPrefix = "reply-$currentSession-"
        engine.setOnUtteranceProgressListener(
            object : UtteranceProgressListener() {
                override fun onStart(utteranceId: String?) = Unit

                override fun onDone(utteranceId: String?) {
                    // 只有"本会话最后一句"自然读完才算读完（中途每句都会 onDone）
                    if (utteranceId != null && utteranceId == lastUtteranceId) {
                        mutableSpeakingTaskId.value = null
                        currentTaskID = null
                        abandonFocus()
                    }
                }

                @Deprecated("旧版接口，部分引擎仍会走")
                override fun onError(utteranceId: String?) {
                    reportError(utteranceId, idPrefix)
                }

                override fun onError(utteranceId: String?, errorCode: Int) {
                    reportError(utteranceId, idPrefix)
                }

                // 本会话任一句被停止（用户点停 / 引擎被别处抢占）都算这一轮结束，
                // 否则 UI 会永远卡在 STOP；上一轮的残留 onStop 前缀不同，不会误清新会话。
                override fun onStop(utteranceId: String?, interrupted: Boolean) {
                    clearIfCurrentSession(utteranceId, idPrefix)
                }
            },
        )
        lastUtteranceId = "$idPrefix${chunks.lastIndex}"
        chunks.forEachIndexed { index, chunk ->
            engine.speak(
                chunk,
                if (index == 0) TextToSpeech.QUEUE_FLUSH else TextToSpeech.QUEUE_ADD,
                null,
                "$idPrefix$index",
            )
        }
    }

    private fun reportError(utteranceId: String?, idPrefix: String) {
        if (utteranceId == null || !utteranceId.startsWith(idPrefix)) return
        report(currentTaskID, "朗读失败：语音引擎报错")
        mutableSpeakingTaskId.value = null
        currentTaskID = null
        abandonFocus()
    }

    private fun clearIfCurrentSession(utteranceId: String?, idPrefix: String) {
        if (utteranceId != null && utteranceId.startsWith(idPrefix)) {
            mutableSpeakingTaskId.value = null
            currentTaskID = null
            abandonFocus()
        }
    }

    private fun report(taskID: String?, message: String) {
        val id = taskID ?: return
        mutableFailure.value = SpeechFailure(id, message)
        failureJob?.cancel()
        failureJob = scope.launch {
            delay(4_000)
            mutableFailure.value = null
        }
    }

    private fun clearFailure() {
        failureJob?.cancel()
        failureJob = null
        mutableFailure.value = null
    }

    private fun rateOf(context: Context): Float =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getFloat(KEY_RATE, 2.0f)

    /** 朗读时让音乐/视频压低，而不是和它们抢音量；失去焦点就直接停。 */
    private fun requestFocus(context: Context) {
        if (focusHeld) return
        val manager = context.getSystemService(AudioManager::class.java) ?: return
        val attributes = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
            .build()
        val request = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_MAY_DUCK)
            .setAudioAttributes(attributes)
            .setOnAudioFocusChangeListener { change ->
                if (change == AudioManager.AUDIOFOCUS_LOSS ||
                    change == AudioManager.AUDIOFOCUS_LOSS_TRANSIENT
                ) {
                    stop()
                }
            }
            .build()
        focusHeld = manager.requestAudioFocus(request) == AudioManager.AUDIOFOCUS_REQUEST_GRANTED
        if (focusHeld) {
            audioManager = manager
            focusRequest = request
        }
    }

    private fun abandonFocus() {
        if (!focusHeld) return
        focusHeld = false
        runCatching {
            focusRequest?.let { audioManager?.abandonAudioFocusRequest(it) }
        }
        audioManager = null
        focusRequest = null
    }

    private const val PREFS = "agentgrid-settings"
    private const val KEY_RATE = "speech-rate"
    private const val SPEECH_PITCH = 1.02f
    private const val LOG_TAG = "ReplySpeech"
}
