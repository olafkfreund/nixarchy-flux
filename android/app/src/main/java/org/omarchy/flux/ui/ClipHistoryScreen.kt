package org.omarchy.flux.ui

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import org.omarchy.flux.core.ClipHistory
import org.omarchy.flux.core.DeviceUi
import org.omarchy.flux.core.FluxCore
import org.omarchy.flux.core.HistoryEntry
import org.omarchy.flux.core.Plugins
import org.omarchy.flux.core.WebUrl

/**
 * This phone's entries of the clipboard history of the computer [d], with
 * a search. The computer searches; the screen shows only the answer to
 * its last request for the text in the field. A tap copies an entry; an
 * uncut link also has Open.
 */
@Composable
fun ClipHistoryScreen(d: DeviceUi, onBack: () -> Unit) {
    var query by rememberSaveable { mutableStateOf("") }
    var retry by remember { mutableIntStateOf(0) }
    var request by rememberSaveable { mutableLongStateOf(0L) }
    var timedOut by remember { mutableStateOf(false) }
    val ready = d.online && d.clipHistorySupported
    LaunchedEffect(query, ready, retry) {
        timedOut = false
        if (!ready) {
            ClipHistory.forget() // a copy in flight can't be answered on a lost link
            return@LaunchedEffect
        }
        // After a rotation the answer to the saved request still holds.
        if (d.clipHistory?.let { it.request == request && it.query == query } == true) return@LaunchedEffect
        if (query.isNotEmpty()) delay(300)
        request = ClipHistory.list(d.id, query) { FluxCore.device(d.id)?.send(it) == true }
    }
    // The no-answer timer runs per request, so a fast answer and new typing never show it.
    LaunchedEffect(request) {
        timedOut = false
        if (request == 0L) return@LaunchedEffect
        delay(10_000)
        timedOut = true
    }
    DisposableEffect(Unit) { onDispose { ClipHistory.forget() } }
    val answer = d.clipHistory?.takeIf { it.request == request && it.query == query }
    Column(Modifier.fillMaxSize().imePadding()) {
        ScreenHeader("History", d.name, onBack)
        when {
            !d.online -> Box(Modifier.padding(horizontal = TiledGutter)) { NotReachable(d, "The clipboard history") }
            !d.clipHistorySupported -> EmptyState(
                Ic.search, "Update Flux on ${d.name}",
                "This version of Flux on ${d.name} does not share its clipboard history.",
                Modifier.padding(top = 48.dp),
            )
            answer?.state == "disabled" -> EmptyState(
                Ic.search, "Clipboard is off for this phone",
                "On ${d.name}, turn on Clipboard for this phone in Flux.",
                Modifier.padding(top = 48.dp),
            )
            else -> {
                SearchField(
                    query, {
                        if (ClipHistory.fits(it)) {
                            query = it
                            timedOut = false
                        }
                    },
                    Modifier.padding(start = TiledGutter, end = TiledGutter, top = 2.dp, bottom = 8.dp),
                    placeholder = "Search clipboard history",
                )
                when {
                    answer == null && timedOut -> HistoryStatus("No answer from ${d.name}") { timedOut = false; retry++ }
                    answer == null -> HistoryStatus("Searching", null)
                    answer.state != "ok" -> HistoryStatus("${d.name} could not read the search") { retry++ }
                    answer.entries.isEmpty() -> HistoryStatus(if (query.isEmpty()) "No clipboard history with ${d.name} yet" else "Nothing found", null)
                    else -> LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
                        items(answer.entries, key = { it.id }) { e -> HistoryRow(d, e) }
                        if (answer.more) {
                            item {
                                T(
                                    "Showing the 50 newest. Search to find older ones.",
                                    Modifier.fillMaxWidth().padding(horizontal = TiledGutter, vertical = 16.dp),
                                    size = 13, color = Tn.sub, align = TextAlign.Center,
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

/** A line under the search field, with Retry when [onRetry] is set. */
@Composable
private fun HistoryStatus(text: String, onRetry: (() -> Unit)?) {
    Column(
        Modifier.fillMaxWidth().padding(top = 24.dp, start = TiledGutter, end = TiledGutter),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        T(text, size = 13, color = Tn.sub, align = TextAlign.Center)
        if (onRetry != null) FluxButton("Retry", onRetry, kind = ButtonKind.Text)
    }
}

/** 1 entry: its text, Sent or Received with the time, and Open for a link. A tap copies it. */
@Composable
private fun HistoryRow(d: DeviceUi, e: HistoryEntry) {
    val context = LocalContext.current
    val meta = buildString {
        append(if (e.sent) "Sent" else "Received")
        timeText(context, e.time).takeIf { it.isNotEmpty() }?.let { append(" · ").append(it) }
        when {
            !e.copyable -> append(" · too large to copy")
            e.truncated && WebUrl.of(e.text) != null -> append(" · too long to open here, copy it")
        }
    }
    val tap = if (e.copyable) Modifier.clickable(onClickLabel = "Copy", role = Role.Button) { copyEntry(d, e) } else Modifier
    Row(
        // One TalkBack item per row, also when it isn't clickable; Open stays its own button.
        Modifier.fillMaxWidth().then(tap).semantics(mergeDescendants = true) {}.padding(horizontal = TiledGutter, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Column(Modifier.weight(1f)) {
            T(e.text, maxLines = 3)
            T(meta, size = 11, color = Tn.sub, maxLines = 1)
        }
        e.link?.let { link -> FluxButton("Open", { openLink(context, link) }, kind = ButtonKind.Text) }
    }
}

/** Copies [e]: at once when it is whole, else after the computer sends the full text. */
private fun copyEntry(d: DeviceUi, e: HistoryEntry) {
    if (!e.truncated) {
        ClipHistory.forget()
        if (Plugins.putRemoteText(FluxCore, d.id, d.name, e.text)) FluxCore.toast("Copied to this phone")
        return
    }
    if (!ClipHistory.copy(d.id, e.id) { FluxCore.device(d.id)?.send(it) == true }) FluxCore.toast("${d.name} is not reachable")
}

/** Opens a web link in the browser. */
private fun openLink(context: Context, link: String) {
    runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(link))) }
        .onFailure { FluxCore.toast("No app opens this link") }
}
