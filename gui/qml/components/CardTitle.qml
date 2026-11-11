import QtQuick
import ".."

// The top row of a card: an icon, the heading, and "Live" while a stream
// runs. A live stream is green, as in Flux for Android.
Row {
  id: root
  property string icon: ""
  property string text: ""
  property bool live: false
  spacing: 8

  Icon {
    anchors.verticalCenter: parent.verticalCenter
    visible: root.icon !== ""
    name: root.icon
    size: 15
    color: root.live ? Theme.ok : Theme.dim
  }
  SectionLabel {
    anchors.verticalCenter: parent.verticalCenter
    text: root.text
  }
  Txt {
    anchors.verticalCenter: parent.verticalCenter
    visible: root.live
    text: "Live"
    color: Theme.ok
    font.pixelSize: 12
  }
}
