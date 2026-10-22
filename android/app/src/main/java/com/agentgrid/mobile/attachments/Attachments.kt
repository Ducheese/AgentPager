package com.agentgrid.mobile.attachments

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import java.io.ByteArrayOutputStream
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable

/**
 * 远程 prompt 附图：手机端压缩 + 上传 Bridge 暂存区 + 文末清单组装。
 * 单图压缩后硬上限 512KB，超了直接判失败；单次最多 10 张（编号到 图10）。
 * 三家 Agent 只收本地绝对路径，零 base64（用户 2026-10-21 定，限额 10-22 定）。
 */
object AttachmentLimits {
    const val MAX_SIDE_PX = 1920
    const val MAX_PER_PROMPT = 10
    const val MAX_UPLOAD_BYTES = 512 * 1024
    /** 卡片缩略图边长：64dp 卡片在 3x 屏上是 192px，留一点余量。 */
    const val THUMB_SIDE_PX = 256

    /** 逐级降质直到落进 512KB；还不够就再降分辨率，见 encodeUnderLimit。 */
    val JPEG_QUALITY_LADDER = intArrayOf(80, 70, 60, 50, 40)
}

enum class AttachmentState {
    COMPRESSING,
    UPLOADING,
    READY,
    FAILED,
}

data class PromptAttachment(
    val clientId: String,
    val fileName: String,
    /** 原相册 URI：压缩失败（含超 1MB）后重试要从这里重新压。 */
    val sourceUri: Uri? = null,
    val jpegBytes: ByteArray? = null,
    /** 压缩参数（q60_1440x810），上传时随 multipart 字段传给 Bridge 拼进文件名。 */
    val meta: String? = null,
    /** 卡片缩略图（JPEG 字节），上传成功、大图字节释放后仍保留。 */
    val thumb: ByteArray? = null,
    val state: AttachmentState = AttachmentState.COMPRESSING,
    /** Bridge 落盘返回的本机绝对路径，READY 后才有。 */
    val localPath: String? = null,
    val error: String? = null,
)

/** 上传返回：只关心本机绝对路径，其它字段透传展示用。 */
@Serializable
data class AttachmentUploadResponse(
    val id: String = "",
    val localPath: String = "",
    val fileName: String = "",
)

/** 压缩结果：字节 + 实际生效的参数，参数会跟着文件名落盘（q60_1440x810）。 */
data class CompressedImage(
    val bytes: ByteArray,
    val quality: Int,
    val width: Int,
    val height: Int,
    /** 卡片缩略图（JPEG，长边 192），READY 后仍保留用于显示。 */
    val thumb: ByteArray,
) {
    val meta: String get() = "q${quality}_${width}x${height}"
}

object ImageCompressor {
    private data class Encoded(val bytes: ByteArray, val quality: Int)

    /**
     * 两步走：inSampleSize 粗降采样防 OOM → 精确缩放到长边≤1920 →
     * 按质量阶梯压到 ≤512KB，压不进就降低分辨率，再不行抛错（调用方转成 FAILED 卡片）。
     * 全部在 Default（CPU）线程跑，调用方自己切。
     */
    suspend fun compress(context: Context, uri: Uri): CompressedImage = withContext(Dispatchers.Default) {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        context.contentResolver.openInputStream(uri)?.use { input ->
            BitmapFactory.decodeStream(input, null, bounds)
        }
        val rawWidth = bounds.outWidth.takeIf { it > 0 } ?: AttachmentLimits.MAX_SIDE_PX
        val rawHeight = bounds.outHeight.takeIf { it > 0 } ?: AttachmentLimits.MAX_SIDE_PX
        val longest = maxOf(rawWidth, rawHeight)
        var sample = 1
        while (longest / (sample * 2) >= AttachmentLimits.MAX_SIDE_PX) sample *= 2
        val decodeOptions = BitmapFactory.Options().apply { inSampleSize = sample }
        val decoded = context.contentResolver.openInputStream(uri)?.use { input ->
            BitmapFactory.decodeStream(input, null, decodeOptions)
        } ?: throw IllegalArgumentException("无法读取图片")
        try {
            val scale = AttachmentLimits.MAX_SIDE_PX.toFloat() / maxOf(decoded.width, decoded.height)
            val scaled = if (scale < 1f) {
                Bitmap.createScaledBitmap(
                    decoded,
                    (decoded.width * scale).toInt().coerceAtLeast(1),
                    (decoded.height * scale).toInt().coerceAtLeast(1),
                    true,
                )
            } else {
                decoded
            }
            try {
                // 先按 1920 长边压；仍超 512KB 就逐步降分辨率，最多降到 960 长边。
                var current = scaled
                try {
                    var encoded = encodeUnderLimit(current)
                    while (encoded == null) {
                        if (maxOf(current.width, current.height) <= 960) {
                            throw IllegalArgumentException(
                                "压缩后仍超过 ${AttachmentLimits.MAX_UPLOAD_BYTES / 1024}KB，请裁小或换一张图",
                            )
                        }
                        val next = Bitmap.createScaledBitmap(
                            current,
                            (current.width * 0.75f).toInt().coerceAtLeast(1),
                            (current.height * 0.75f).toInt().coerceAtLeast(1),
                            true,
                        )
                        if (current !== scaled) current.recycle()
                        current = next
                        encoded = encodeUnderLimit(current)
                    }
                    CompressedImage(
                        bytes = encoded.bytes,
                        quality = encoded.quality,
                        width = current.width,
                        height = current.height,
                        thumb = makeThumb(current),
                    )
                } finally {
                    if (current !== scaled) current.recycle()
                }
            } finally {
                if (scaled !== decoded) scaled.recycle()
            }
        } finally {
            decoded.recycle()
        }
    }

    private fun encodeUnderLimit(bitmap: Bitmap): Encoded? {
        val output = ByteArrayOutputStream()
        for (quality in AttachmentLimits.JPEG_QUALITY_LADDER) {
            output.reset()
            bitmap.compress(Bitmap.CompressFormat.JPEG, quality, output)
            if (output.size() <= AttachmentLimits.MAX_UPLOAD_BYTES) {
                return Encoded(output.toByteArray(), quality)
            }
        }
        return null
    }

    private fun makeThumb(bitmap: Bitmap): ByteArray {
        // 先居中裁成正方形再缩放：卡片是 1:1 的 Crop，方形缩略图才能 1:1 显示，
        // 横图直接缩长边的话放进去会被放大 1.7 倍，看着就是糊的。
        val side = minOf(bitmap.width, bitmap.height)
        val square = Bitmap.createBitmap(
            bitmap,
            (bitmap.width - side) / 2,
            (bitmap.height - side) / 2,
            side,
            side,
        )
        val scaled = Bitmap.createScaledBitmap(
            square,
            AttachmentLimits.THUMB_SIDE_PX,
            AttachmentLimits.THUMB_SIDE_PX,
            true,
        )
        try {
            val output = ByteArrayOutputStream()
            scaled.compress(Bitmap.CompressFormat.JPEG, 85, output)
            return output.toByteArray()
        } finally {
            if (scaled !== square && scaled !== bitmap) scaled.recycle()
            if (square !== bitmap) square.recycle()
        }
    }
}

/**
 * 文本里的 [图N] 占位符。两条不变式：
 * 1. N 永远等于图片条里的序号（1-based）：删图后整篇重排，被删的占位符去掉，
 *    比它大的编号减一，再插新的也不会撞号；
 * 2. 占位符不可分割：输入法在块内退格/选区删除，扩成整块删除，不留 `[图` 或 `图1]`。
 */
object PromptTokens {
    private val TOKEN = Regex("""\[图(\d+)]""")

    data class AtomicDeletion(val text: String, val cursor: Int)

    /** 删掉第 removedNumber 张（1-based）对应的占位符，其余编号往前挪。 */
    fun renumberAfterRemoval(text: String, removedNumber: Int): String {
        if (removedNumber <= 0 || !TOKEN.containsMatchIn(text)) return text
        val out = StringBuilder(text.length)
        var cursor = 0
        val seams = mutableListOf<Int>()
        for (match in TOKEN.findAll(text)) {
            val number = match.groupValues[1].toIntOrNull() ?: continue
            out.append(text, cursor, match.range.first)
            when {
                number == removedNumber -> seams += out.length
                number > removedNumber -> out.append("[图").append(number - 1).append(']')
                else -> out.append(match.value)
            }
            cursor = match.range.last + 1
        }
        out.append(text, cursor, text.length)
        // 占位符两侧都是空格时只留一个，别留出双空格；首尾的孤立空格一并收掉。
        for (seam in seams.asReversed()) {
            if (seam in 1 until out.length && out[seam - 1] == ' ' && out[seam] == ' ') {
                out.deleteCharAt(seam)
            }
        }
        return out.toString().trim(' ')
    }

    /**
     * 输入法这次是删除（而不是输入）：若删到了 [图N] 的任何一个字符，就把整个占位符删掉。
     * 返回 null 表示这次编辑没碰占位符（含纯输入、删占位符左/右相邻的空格）。
     */
    fun expandAtomicDeletion(oldText: String, newText: String): AtomicDeletion? {
        if (newText.length >= oldText.length) return null
        val prefix = commonPrefix(oldText, newText)
        val suffix = commonSuffix(oldText, newText, prefix)
        val deletedEnd = oldText.length - suffix
        if (deletedEnd <= prefix) return null
        val token = TOKEN.findAll(oldText).firstOrNull { match ->
            match.range.first < deletedEnd && match.range.last + 1 > prefix
        } ?: return null
        val start = token.range.first
        return AtomicDeletion(
            text = oldText.removeRange(start, token.range.last + 1),
            cursor = start,
        )
    }

    private fun commonPrefix(a: String, b: String): Int {
        val limit = minOf(a.length, b.length)
        var i = 0
        while (i < limit && a[i] == b[i]) i++
        return i
    }

    private fun commonSuffix(a: String, b: String, prefix: Int): Int {
        val limit = minOf(a.length, b.length) - prefix
        var i = 0
        while (i < limit && a[a.length - 1 - i] == b[b.length - 1 - i]) i++
        return i
    }
}

object PromptAssembler {
    /**
     * 组装终态 prompt：原文原样保留，文末追加路径清单。
     * 发送前已保证全部附件 READY，所以这里的序号与图片条序号（列表下标 + 1）一致。
     * 若混进非 READY（不该发生），按 READY 过滤兜底，不让发不出去的图占号。
     */
    fun assemble(prompt: String, attachments: List<PromptAttachment>): String {
        val ready = attachments.filter { it.state == AttachmentState.READY && it.localPath != null }
        if (ready.isEmpty()) return prompt
        val lines = ready.mapIndexed { index, attachment ->
            "图${index + 1}: \"${attachment.localPath}\""
        }
        return buildString {
            // 尾部空白先裁掉：清单必须以一个空行开头，保留用户的尾随换行会多出空行。
            append(prompt.trimEnd())
            append("\n\n[附图清单]:")
            lines.forEach { append("\n- ").append(it) }
        }
    }
}
