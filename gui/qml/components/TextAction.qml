import QtQuick
import ".."

// A small action in a line of text, in the accent, such as Cancel on a
// file that goes to the device.
Txt {
  id: root
  signal clicked()
  activeFocusOnTab: true
  Accessible.role: Accessible.Button
  Accessible.name: text
  Keys.onReturnPressed: clicked()
  Keys.onSpacePressed: clicked()

  color: Theme.accent
  font.pixelSize: 12
  font.weight: Font.DemiBold
  font.underline: area.containsMouse || activeFocus

  MouseArea {
    id: area
    anchors.fill: parent
    anchors.margins: -4
    hoverEnabled: true
    cursorShape: Qt.PointingHandCursor
    onClicked: root.clicked()
  }
}
