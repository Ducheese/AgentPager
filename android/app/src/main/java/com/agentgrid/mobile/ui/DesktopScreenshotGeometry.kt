package com.agentgrid.mobile.ui

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size

internal object DesktopScreenshotGeometry {
    fun toImageNormalized(
        tap: Offset,
        container: Size,
        displayed: Size,
        scale: Float,
        translation: Offset,
    ): Offset? {
        if (container.width <= 0f || container.height <= 0f ||
            displayed.width <= 0f || displayed.height <= 0f || scale <= 0f
        ) {
            return null
        }

        val centerX = container.width / 2f
        val centerY = container.height / 2f
        val imageXAtScaleOne = centerX + (tap.x - centerX - translation.x) / scale
        val imageYAtScaleOne = centerY + (tap.y - centerY - translation.y) / scale
        val imageLeft = (container.width - displayed.width) / 2f
        val imageTop = (container.height - displayed.height) / 2f
        val normalizedX = (imageXAtScaleOne - imageLeft) / displayed.width
        val normalizedY = (imageYAtScaleOne - imageTop) / displayed.height
        if (normalizedX !in 0f..1f || normalizedY !in 0f..1f) return null
        return Offset(normalizedX, normalizedY)
    }
}
