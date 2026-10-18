package com.agentgrid.mobile

import android.content.Context
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import android.util.Log
import java.util.Locale
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * REPLY 朗读（系统 TTS）。进程级单例：不绑定 Activity/ViewModel，
 * 界面退后台、Activity 被回收都不打断播放；配合前台保活服务，进程不被冻结即可续读。
 *
 * 停止只有三条路：再点一次 REPLY 标签、通知栏「停止朗读」、解除配对。
 */
object ReplySpeech {
    private var tts: TextToSpeech? = null
    private var engineReady = false
    private var pendingSpeech: (() -> Unit)? = null
    /** 每轮朗读一个自增会话号：utterance id 全局唯一，上一轮的迟到回调不会误清新一轮状态。 */
    private var session = 0L
    private var lastUtteranceId: String? = null

    private val mutableSpeakingTaskId = MutableStateFlow<String?>(null)
    val speakingTaskId: StateFlow<String?> = mutableSpeakingTaskId.asStateFlow()

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
        mutableSpeakingTaskId.value = taskId
        ensureEngine(context) { startUtterances(chunks) }
    }

    fun stop() {
        mutableSpeakingTaskId.value = null
        pendingSpeech = null
        lastUtteranceId = null
        runCatching { tts?.stop() }
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
                // 绑定失败（典型：没声明 <queries> 时第三方引擎不可见）。清掉实例让下次点击能重试。
                Log.e(LOG_TAG, "TTS 初始化失败 status=$status")
                runCatching { tts?.shutdown() }
                tts = null
                pendingSpeech = null
                mutableSpeakingTaskId.value = null
                return@TextToSpeech
            }
            tts?.apply {
                val language = setLanguage(Locale.SIMPLIFIED_CHINESE)
                if (language == TextToSpeech.LANG_MISSING_DATA ||
                    language == TextToSpeech.LANG_NOT_SUPPORTED
                ) {
                    setLanguage(Locale.CHINA)
                }
                // 语速/音调必须显式钉死：第三方引擎（MultiTTS 等）对"从未设置"的默认值处理不可靠，
                // 会出现语速失控。这组参数与 keep-run-coach 在同一台设备、同一 MultiTTS 上实测通过。
                setSpeechRate(SPEECH_RATE)
                setPitch(SPEECH_PITCH)
            }
            val pending = pendingSpeech
            pendingSpeech = null
            pending?.invoke()
        }
    }

    private fun startUtterances(chunks: List<String>) {
        val engine = tts ?: return
        // 会话号先自增再下发：QUEUE_FLUSH 打断旧句时旧 id 前缀已是上一轮，天然不会被误认
        val currentSession = ++session
        val idPrefix = "reply-$currentSession-"
        engine.setOnUtteranceProgressListener(
            object : UtteranceProgressListener() {
                override fun onStart(utteranceId: String?) = Unit

                override fun onDone(utteranceId: String?) {
                    // 只有"本会话最后一句"自然读完才算读完（中途每句都会 onDone）
                    if (utteranceId != null && utteranceId == lastUtteranceId) {
                        mutableSpeakingTaskId.value = null
                    }
                }

                @Deprecated("旧版接口，部分引擎仍会走")
                override fun onError(utteranceId: String?) {
                    clearIfCurrentSession(utteranceId, idPrefix)
                }

                override fun onError(utteranceId: String?, errorCode: Int) {
                    clearIfCurrentSession(utteranceId, idPrefix)
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

    private fun clearIfCurrentSession(utteranceId: String?, idPrefix: String) {
        if (utteranceId != null && utteranceId.startsWith(idPrefix)) {
            mutableSpeakingTaskId.value = null
        }
    }

    /**
     * 语速/音调必须显式钉死：第三方引擎（MultiTTS 等）对"从未设置"的默认值处理不可靠（语速会失控）。
     * 语速 2.0 = 用户指定档位；音调 1.02 微亮。
     */
    private const val SPEECH_RATE = 2.0f
    private const val SPEECH_PITCH = 1.02f
    private const val LOG_TAG = "ReplySpeech"
}
