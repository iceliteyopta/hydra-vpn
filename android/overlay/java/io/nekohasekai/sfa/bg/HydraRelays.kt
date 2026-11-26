package io.nekohasekai.sfa.bg

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.net.URI

internal object HydraRelays {
    data class Relay(val host: String, val port: Int?, val wifiOnly: Boolean)
    data class Runtime(val content: String, val owners: Map<String, String>)

    private fun prefs(context: Context) = context.getSharedPreferences("hydra_relays", Context.MODE_PRIVATE)
    private fun key(id: Long, tag: String) = "$id/$tag"

    fun read(context: Context, id: Long, tag: String): Relay? {
        val json = prefs(context).getString(key(id, tag), null)?.let(::JSONObject) ?: return null
        return Relay(json.getString("host"), json.optInt("port").takeIf { it > 0 }, json.optBoolean("wifi"))
    }

    fun save(context: Context, id: Long, tag: String, relay: Relay?) {
        val edit = prefs(context).edit()
        if (relay == null) edit.remove(key(id, tag))
        else edit.putString(key(id, tag), JSONObject().put("host", relay.host).put("port", relay.port).put("wifi", relay.wifiOnly).toString())
        check(edit.commit())
    }

    fun host(input: String): String? {
        val raw = input.trim().removeSurrounding("[", "]")
        if (raw.isEmpty() || raw.any { it.isWhitespace() || it in "/?#@%" }) return null
        val authority = if (':' in raw) "[$raw]" else raw
        return runCatching { URI("udp://$authority").host?.removeSurrounding("[", "]") }.getOrNull()
    }

    fun tunnels(config: JSONObject): List<JSONObject> {
        val items = config.optJSONArray("outbounds") ?: return emptyList()
        return (0 until items.length()).mapNotNull(items::optJSONObject).filter {
            it.optString("type") == "hysteria2" && it.optString("tag").isNotBlank() &&
                it.optString("server").isNotBlank() && it.optString("detour").isBlank()
        }
    }

    fun canBindWifi(config: JSONObject, tunnel: JSONObject): Boolean =
        config.optJSONObject("route")?.optBoolean("auto_detect_interface") == true &&
            config.optJSONObject("route")?.optString("default_interface").isNullOrBlank() &&
            !tunnel.optBoolean("tcp_fast_open") &&
            listOf("bind_interface", "inet4_bind_address", "inet6_bind_address").none(tunnel::has)

    fun prepare(context: Context, id: Long, original: String): Runtime {
        val config = JSONObject(original)
        val owners = mutableMapOf<String, String>()
        val occupied = mutableSetOf<String>()
        for (field in listOf("outbounds", "endpoints")) {
            val array = config.optJSONArray(field) ?: continue
            for (i in 0 until array.length()) occupied += array.optJSONObject(i)?.optString("tag").orEmpty()
        }
        fun reserve(base: String): String {
            var tag = base
            var n = 2
            while (!occupied.add(tag)) tag = "$base " + n++
            return tag
        }
        val outbounds = config.optJSONArray("outbounds") ?: return Runtime(original, owners)
        for (direct in tunnels(config)) {
            val tag = direct.getString("tag")
            val relay = read(context, id, tag) ?: continue
            owners[tag] = tag
            if (!context.getSharedPreferences("hydra", 0).getBoolean("recover", true) ||
                (relay.wifiOnly && !canBindWifi(config, direct))) continue
            val via = JSONObject(direct.toString())
            val primaryTag = reserve("$tag · primary")
            val relayTag = reserve("$tag · relay")
            via.put("tag", relayTag).put("server", relay.host)
            via.optJSONObject("tls")?.let {
                if (it.optString("server_name").isBlank()) it.put("server_name", direct.getString("server"))
            }
            relay.port?.let { via.remove("server_ports"); via.put("server_port", it) }
            if (relay.wifiOnly) {
                via.put("network_strategy", "default").put("network_type", JSONArray().put("wifi"))
                via.remove("fallback_network_type")
                via.remove("fallback_delay")
            }
            val group = JSONObject().put("type", "urltest").put("tag", tag)
                .put("outbounds", JSONArray().put(primaryTag).put(relayTag))
                .put("url", "https://1.1.1.1/cdn-cgi/trace").put("interval", "30s")
                // A reachable path wins over a dead one. Tiny latency changes should not move sessions.
                .put("tolerance", 15000).put("interrupt_exist_connections", true)
            val index = (0 until outbounds.length()).first { outbounds.optJSONObject(it) === direct }
            outbounds.put(index, group)
            outbounds.put(direct.put("tag", primaryTag)).put(via)
            owners[primaryTag] = tag
            owners[relayTag] = tag
        }
        return Runtime(if (owners.isEmpty()) original else config.toString(), owners)
    }
}
