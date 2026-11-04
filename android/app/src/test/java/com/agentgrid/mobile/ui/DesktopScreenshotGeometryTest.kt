package com.agentgrid.mobile.ui

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class DesktopScreenshotGeometryTest {
    private val container = Size(1000f, 500f)
    private val displayed = Size(1000f, 500f)

    @Test
    fun `未缩放时命中图心`() {
        val normalized = DesktopScreenshotGeometry.toImageNormalized(
            tap = Offset(500f, 250f),
            container = container,
            displayed = displayed,
            scale = 1f,
            translation = Offset.Zero,
        )

        assertEquals(0.5f, normalized!!.x, 0.0001f)
        assertEquals(0.5f, normalized.y, 0.0001f)
    }

    @Test
    fun `缩放后仍映射回原图坐标`() {
        val normalized = DesktopScreenshotGeometry.toImageNormalized(
            tap = Offset(250f, 250f),
            container = container,
            displayed = displayed,
            scale = 2f,
            translation = Offset.Zero,
        )

        assertEquals(0.375f, normalized!!.x, 0.0001f)
        assertEquals(0.5f, normalized.y, 0.0001f)
    }

    @Test
    fun `平移后仍映射回原图坐标`() {
        val normalized = DesktopScreenshotGeometry.toImageNormalized(
            tap = Offset(500f, 250f),
            container = container,
            displayed = displayed,
            scale = 2f,
            translation = Offset(100f, 0f),
        )

        assertEquals(0.45f, normalized!!.x, 0.0001f)
        assertEquals(0.5f, normalized.y, 0.0001f)
    }

    @Test
    fun `留边叠加缩放和平移后仍映射正确`() {
        val normalized = DesktopScreenshotGeometry.toImageNormalized(
            tap = Offset(600f, 600f),
            container = Size(1000f, 1000f),
            displayed = Size(1000f, 500f),
            scale = 2f,
            translation = Offset(100f, 50f),
        )

        assertEquals(0.5f, normalized!!.x, 0.0001f)
        assertEquals(0.55f, normalized.y, 0.0001f)
    }

    @Test
    fun `黑边不产生点击目标`() {
        val squareContainer = Size(1000f, 1000f)
        val wideImage = Size(1000f, 500f)
        val center = DesktopScreenshotGeometry.toImageNormalized(
            tap = Offset(500f, 500f),
            container = squareContainer,
            displayed = wideImage,
            scale = 1f,
            translation = Offset.Zero,
        )
        val blackBar = DesktopScreenshotGeometry.toImageNormalized(
            tap = Offset(500f, 100f),
            container = squareContainer,
            displayed = wideImage,
            scale = 1f,
            translation = Offset.Zero,
        )

        assertEquals(0.5f, center!!.x, 0.0001f)
        assertEquals(0.5f, center.y, 0.0001f)
        assertNull(blackBar)
    }
}
