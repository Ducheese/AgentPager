package com.agentgrid.mobile

import android.media.AudioTrack
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SoundEngineTest {
    @Test
    fun `静态音轨尚无数据时允许写入`() {
        assertTrue(
            AudioTrackStatePolicy.canWrite(
                AudioTrack.MODE_STATIC,
                AudioTrack.STATE_NO_STATIC_DATA,
            ),
        )
    }

    @Test
    fun `流式音轨只有初始化后才允许写入`() {
        assertTrue(
            AudioTrackStatePolicy.canWrite(
                AudioTrack.MODE_STREAM,
                AudioTrack.STATE_INITIALIZED,
            ),
        )
        assertFalse(
            AudioTrackStatePolicy.canWrite(
                AudioTrack.MODE_STREAM,
                AudioTrack.STATE_UNINITIALIZED,
            ),
        )
    }

    @Test
    fun `SoundTheme 解析及回退`() {
        assertEquals(SoundTheme.RIMWORLD, SoundTheme.fromName("rimworld"))
        assertEquals(SoundTheme.RIMWORLD, SoundTheme.fromName("RIMWORLD"))
        assertEquals(SoundTheme.ARCADE, SoundTheme.fromName("arcade"))
        assertEquals(SoundTheme.RIMWORLD, SoundTheme.fromName("unknown"))
        assertEquals(SoundTheme.RIMWORLD, SoundTheme.fromName(null))
    }
}
