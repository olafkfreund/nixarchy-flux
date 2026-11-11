import QtQuick
import ".."

// 1 entry of the chat: a clip, a link, or a file. An entry from the device
// sits on the left, on bg2. An entry from this computer sits on the right,
// on the sent fill. The top row holds the kind, the state word in its
// color, the time, and the actions.
//
// entry is {key, at, clip} or {key, at, file}: clip is an entry of
// state.clipboard, and file is an entry of state.transfers.
Item {
  id: root
  property var view: null
  property var entry: ({})
  // The widest that the card can be.
  property real maxWidth: 600

  readonly property var clip: entry.clip || null
  readonly property var file: entry.file || null
  readonly property bool mine: clip ? clip.dir === "out" : (!!file && file.dir === "out")
  readonly property string devName: view ? view.devName : "the device"

  readonly property string source: clip ? (clip.source || "") : ""
  readonly property bool isLink: source === "link"
  readonly property bool isImageClip: !!clip && !!clip.image
  // The web link of a clip that is only a link, or "".
  readonly property string link: clip && !isImageClip ? Fmt.webLink(clip.text) : ""

  readonly property bool moving: !!file && ["waiting", "queued", "active"].indexOf(file.state) >= 0
  readonly property real progress: !file ? 0 : (file.size > 0 ? (file.done || 0) / file.size : (file.state === "done" ? 1 : 0))
  // A file that is an image on this computer: a sent image, or a received
  // image after the last byte.
  readonly property bool fileImage: !!file && !!file.path && Fmt.isImage(file.path) && (mine ? file.state !== "failed" : file.state === "done")

  readonly property string kindIcon: file ? (Fmt.isImage(file.name) ? "image" : "file")
    : isLink ? "web" : isImageClip ? "image" : source === "scan" ? "scan" : "clipboard"

  // The state word and its color.
  readonly property var word: {
    if (file) {
      switch (file.state) {
      case "active": return [(mine ? "Sending " : "Receiving ") + Math.floor(100 * progress) + "%", Theme.accent]
      case "queued": return ["Queued", Theme.warn]
      case "waiting": return ["Waits for " + devName, Theme.warn]
      case "failed": return ["Failed", Theme.err]
      case "canceled": return ["Canceled", root.dim]
      case "done": return [mine ? "Sent" : "Received", mine ? Theme.accent : Theme.ok]
      }
      return [file.state || "", root.dim]
    }
    if (!clip) return ["", root.dim]
    if (mine) {
      if (isLink) return ["Link sent", Theme.accent]
      return [clip.device ? "Sent" : "Copied", Theme.accent]
    }
    if (isLink) return ["Opened", Theme.ok]
    if (source === "scan") return ["Scanned", Theme.ok]
    if (source === "share") return ["Shared", Theme.ok]
    return ["Copied", Theme.ok]
  }

  // The line under the body, or "".
  readonly property string detail: {
    if (file) {
      if (file.error) return ""
      var parts = [Fmt.bytes(file.size)]
      if (file.state === "active" && file.rate > 0) parts.push(Fmt.rate(file.rate))
      if (file.state === "waiting" || file.state === "queued") parts.push(mine ? "in the outbox" : "")
      return parts.filter(function (p) { return p !== "" }).join(" · ")
    }
    if (!clip) return ""
    if (mine && isLink) return devName + " shows it in a notification."
    if (clip.truncated) return "Long text. Copy takes all " + Fmt.bytes(clip.size) + "."
    return ""
  }

  // The text of a clip, without the empty lines at the start and the end.
  readonly property string body: clip && !isImageClip ? String(clip.text || "").slice(0, 4000).replace(/^\s+|\s+$/g, "") : ""

  // The second ink on the fill of the card.
  readonly property color dim: mine ? Theme.sentDim : Theme.dim

  readonly property real pad: 12
  readonly property real minWidth: 240
  // The width of the card from its content, up to maxWidth.
  readonly property real natural: {
    var top = meta.implicitWidth + 16 + actions.implicitWidth
    var content = file ? 420 : (isImageClip ? 280 : measure.implicitWidth)
    return Math.max(top, content, detailText.implicitWidth) + 2 * pad
  }

  implicitHeight: card.height

  function copy() {
    var v = view
    v.call("clipboard.copy", { id: clip.id }, function () { v.toast("Copied to the clipboard") })
  }

  // The folder of a path as a file URL.
  function folderUrl(path) {
    return Fmt.fileUrl(path.slice(0, Math.max(1, path.lastIndexOf("/"))))
  }

  // The body of a clip on 1 line, for the width of the card.
  Txt {
    id: measure
    visible: false
    text: root.body.split("\n")[0].slice(0, 200)
  }

  Rectangle {
    id: card
    x: root.mine ? root.width - width : 0
    width: Math.min(root.maxWidth, root.width, Math.max(root.minWidth, root.natural))
    height: col.implicitHeight + 2 * 10
    color: root.mine ? Theme.sent : Theme.bg2
    border.width: 1
    border.color: root.mine ? Theme.sentEdge : Theme.bg3

    Column {
      id: col
      x: root.pad
      y: 10
      width: card.width - 2 * root.pad
      spacing: 6

      // The kind, the state, and the time. The actions are at the end.
      Item {
        width: parent.width
        height: Math.max(meta.implicitHeight, actions.implicitHeight)

        Row {
          id: meta
          anchors.verticalCenter: parent.verticalCenter
          spacing: 6
          Icon {
            anchors.verticalCenter: parent.verticalCenter
            name: root.kindIcon
            size: 13
            color: root.word[1]
          }
          Txt {
            anchors.verticalCenter: parent.verticalCenter
            text: root.word[0]
            color: root.word[1]
            font.pixelSize: 12
            font.weight: Font.DemiBold
          }
          Txt {
            anchors.verticalCenter: parent.verticalCenter
            text: Fmt.clock(root.entry.at)
            color: root.dim
            font.pixelSize: 11
          }
        }

        Row {
          id: actions
          anchors.right: parent.right
          anchors.verticalCenter: parent.verticalCenter
          spacing: 2

          IconAction {
            ink: root.dim
            visible: root.link !== ""
            icon: "open"
            tip: "Open in the browser"
            onClicked: Qt.openUrlExternally(root.link)
          }
          IconAction {
            ink: root.dim
            visible: !!root.clip
            icon: "copy"
            tip: "Copy"
            onClicked: root.copy()
          }
          IconAction {
            ink: root.dim
            visible: !!root.clip
            icon: root.clip && root.clip.pinned ? "pin" : "pin-outline"
            on: !!root.clip && !!root.clip.pinned
            tip: on ? "Remove from saved" : "Save. Saved clips stay after a restart."
            onClicked: root.view.call(on ? "clipboard.unpin" : "clipboard.pin", { id: root.clip.id })
          }
          IconAction {
            ink: root.dim
            visible: !!root.clip
            icon: "trash"
            tip: "Delete"
            onClicked: root.view.call("clipboard.delete", { id: root.clip.id })
          }

          // The actions of a file are words, because each one has its own
          // state.
          Row {
            visible: !!root.file
            anchors.verticalCenter: parent.verticalCenter
            spacing: 14
            TextAction {
              visible: !!root.file && root.file.state === "waiting"
              text: "Retry now"
              onClicked: root.view.call("transfer.retry", { id: root.file.id })
            }
            TextAction {
              visible: root.moving
              text: "Cancel"
              onClicked: root.view.call("transfer.cancel", { id: root.file.id })
            }
            TextAction {
              visible: !!root.file && !root.mine && root.file.state === "done" && !!root.file.path
              text: "Show in folder"
              onClicked: Qt.openUrlExternally(root.folderUrl(root.file.path))
            }
          }
        }
      }

      // A text or a link. A link opens in the browser.
      Txt {
        id: bodyText
        visible: root.body !== ""
        width: parent.width
        text: root.body
        color: root.link !== "" ? Theme.accent : Theme.fg
        font.underline: root.link !== "" && linkArea.containsMouse
        wrapMode: Text.WrapAtWordBoundaryOrAnywhere
        maximumLineCount: 8
        elide: Text.ElideRight
        MouseArea {
          id: linkArea
          anchors.fill: parent
          enabled: root.link !== ""
          hoverEnabled: true
          cursorShape: Qt.PointingHandCursor
          onClicked: Qt.openUrlExternally(root.link)
        }
      }

      // A file name from the phone can hide its real extension with bidi
      // controls, so the card shows them as codes.
      Txt {
        visible: !!root.file
        width: parent.width
        text: root.file ? Fmt.showControls(root.file.name) : ""
        elide: Text.ElideMiddle
      }

      Image {
        id: picture
        visible: root.isImageClip || root.fileImage
        width: Math.min(280, parent.width)
        height: visible ? (status === Image.Ready && implicitWidth > 0 ? Math.min(200, width * implicitHeight / implicitWidth) : 120) : 0
        source: root.isImageClip ? Fmt.fileUrl(root.clip.image) : (root.fileImage ? Fmt.fileUrl(root.file.path) : "")
        sourceSize.width: 560
        fillMode: Image.PreserveAspectFit
        horizontalAlignment: Image.AlignLeft
        asynchronous: true
      }

      Bar {
        visible: root.moving
        width: parent.width
        height: 3
        value: root.progress
      }

      Txt {
        id: detailText
        visible: text !== ""
        width: parent.width
        text: root.detail
        color: root.dim
        font.pixelSize: 11
        wrapMode: Text.Wrap
      }

      Txt {
        visible: !!root.file && !!root.file.error
        width: parent.width
        text: root.file ? (root.file.error || "") : ""
        color: Theme.err
        font.pixelSize: 11
        wrapMode: Text.Wrap
        maximumLineCount: 3
        elide: Text.ElideRight
      }
    }
  }
}
