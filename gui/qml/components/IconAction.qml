import QtQuick
import QtQuick.Controls
import ".."

// A small icon button with a tooltip, for the actions of a chat entry.
// on shows the state of a switch, for example a saved clip.
Rectangle {
  id: root
  property string icon: ""
  property string tip: ""
  property bool on: false
  // The color of the icon at rest.
  property color ink: Theme.dim
  signal clicked()
  activeFocusOnTab: true
  Accessible.role: Accessible.Button
  Accessible.name: tip
  Keys.onReturnPressed: clicked()
  Keys.onSpacePressed: clicked()

  implicitWidth: 26
  implicitHeight: 24
  color: area.containsMouse ? Theme.alpha(Theme.fg, 0.08) : "transparent"
  border.width: activeFocus ? 1 : 0
  border.color: Theme.accent

  Icon {
    anchors.centerIn: parent
    name: root.icon
    size: 14
    color: root.on || area.containsMouse || root.activeFocus ? Theme.accent : root.ink
  }

  MouseArea {
    id: area
    anchors.fill: parent
    hoverEnabled: true
    cursorShape: Qt.PointingHandCursor
    onClicked: root.clicked()
  }

  // The tip is plain text, like the tips of the rail.
  ToolTip {
    id: tipBox
    visible: area.containsMouse && root.tip !== ""
    delay: 500
    text: root.tip
    contentItem: Text {
      text: tipBox.text
      font: tipBox.font
      color: tipBox.palette.toolTipText
      textFormat: Text.PlainText
    }
  }
}
