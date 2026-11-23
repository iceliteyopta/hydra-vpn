package io.nekohasekai.sfa.compose.screen.settings

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.Row
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import io.nekohasekai.sfa.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import java.net.URL

private const val repo = "iceliteyopta/hydra-vpn"

@Composable
fun HydraReleaseCheck(manual: Boolean = false, finished: () -> Unit = {}) {
    val ctx = LocalContext.current
    val prefs = ctx.getSharedPreferences("hydra", 0)
    var url by remember { mutableStateOf("") }
    var status by remember { mutableIntStateOf(0) }
    LaunchedEffect(Unit) {
        if (!manual && !prefs.getBoolean("updates", true)) return@LaunchedEffect
        try {
            val current = ctx.packageManager.getPackageInfo(ctx.packageName, 0).versionCode
            url = withContext(Dispatchers.IO) {
                val conn = URL("https://api.github.com/repos/$repo/releases?per_page=100").openConnection()
                conn.connectTimeout = 8000
                conn.readTimeout = 8000
                val rows = JSONArray(conn.getInputStream().bufferedReader().use { it.readText() })
                var newest = current
                var link = ""
                for (i in 0 until rows.length()) {
                    val release = rows.getJSONObject(i)
                    if (release.optBoolean("draft") || release.optBoolean("prerelease")) continue
                    val tag = release.optString("tag_name")
                    if (!tag.startsWith("android-")) continue
                    val code = tag.removePrefix("android-").toIntOrNull() ?: continue
                    if (code <= newest) continue
                    val assets = release.getJSONArray("assets")
                    val expected = "https://github.com/$repo/releases/download/$tag/Hydra-Mobile.apk"
                    if ((0 until assets.length()).any { assets.getJSONObject(it).optString("browser_download_url") == expected }) {
                        newest = code
                        link = expected
                    }
                }
                link
            }
            if (url.isNotEmpty()) status = R.string.hydra_new else if (manual) status = R.string.hydra_latest
        } catch (e: java.util.concurrent.CancellationException) { throw e }
        catch (e: Exception) { if (manual) status = R.string.hydra_check_failed }
    }
    if (status != 0) {
        val close = { status = 0; finished() }
        AlertDialog(onDismissRequest = close, text = { Text(stringResource(status)) },
            confirmButton = { TextButton(onClick = {
                if (url.isNotBlank()) ctx.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
                close()
            }) { Text(stringResource(if (url.isBlank()) android.R.string.ok else R.string.hydra_download)) } },
            dismissButton = { if (url.isNotBlank()) TextButton(onClick = close) { Text(stringResource(android.R.string.cancel)) } })
    }
}

@Composable
fun HydraMobileOptions() {
    val prefs = LocalContext.current.getSharedPreferences("hydra", 0)
    for ((key, label) in listOf("updates" to R.string.hydra_updates, "recover" to R.string.hydra_recover)) {
        var value by remember(key) { mutableStateOf(prefs.getBoolean(key, true)) }
        Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
            Text(stringResource(label), modifier = androidx.compose.ui.Modifier.weight(1f))
            Switch(checked = value, onCheckedChange = { value = it; prefs.edit().putBoolean(key,it).apply() })
        }
    }
    var manual by remember { mutableStateOf(false) }
    TextButton(enabled = !manual, onClick = { manual = true }) { Text(stringResource(R.string.hydra_check)) }
    if (manual) HydraReleaseCheck(true) { manual = false }
}
