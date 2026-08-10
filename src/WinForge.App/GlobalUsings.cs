// The App project enables both WPF and Windows Forms (the latter only for the
// FolderBrowserDialog used by IFilePicker.PickFolder). Windows Forms implicitly
// imports System.Windows.Forms, which makes the simple names "UserControl" and
// "Application" ambiguous with their WPF counterparts. These aliases pin the
// simple names to the WPF types used throughout the codebase; the Windows Forms
// types remain reachable via their fully-qualified names where needed.
global using UserControl = System.Windows.Controls.UserControl;
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
