package org.omarchy.flux.core

import java.net.InetAddress
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DeviceTest {
    private fun ip(s: String) = InetAddress.getByName(s) // literals only: no lookup

    @Test
    fun aLoopbackLinkKeepsTheStoredAddress() {
        assertEquals("192.168.1.20", nextLastIp("192.168.1.20", ip("127.0.0.1")))
        assertEquals("192.168.1.20", nextLastIp("192.168.1.20", ip("::1")))
        assertEquals("", nextLastIp("", ip("127.0.0.1")))
        assertEquals("192.168.1.30", nextLastIp("192.168.1.20", ip("192.168.1.30")))
        assertEquals("100.64.0.3", nextLastIp("192.168.1.20", ip("100.64.0.3")))
        assertEquals("192.168.1.20", nextLastIp("192.168.1.20", null))
    }

    @Test
    fun loopbackText() {
        for (s in listOf("127.0.0.1", "127.1.2.3", "::1", "0:0:0:0:0:0:0:1", ip("::1").hostAddress)) assertTrue(s, isLoopbackText(s))
        for (s in listOf("", "192.168.1.20", "100.64.0.1", "fe80::1", "10.127.0.1")) assertFalse(s, isLoopbackText(s))
    }
}
