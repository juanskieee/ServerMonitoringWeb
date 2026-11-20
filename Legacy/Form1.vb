Imports System.IO
Imports System.Media
Imports System.Net.NetworkInformation
Imports System.Reflection.Emit
Imports System.Security.Cryptography
Imports System.Text.Json
Imports System.Windows

Public Class Form1
    Private WithEvents pingTimer As New Timer()
    Private WithEvents blinkTimer As New Timer()

    Dim SERVER_1 As String
    Dim SERVER_2 As String
    Dim SERVER_3 As String
    Dim SERVER_4 As String
    Dim SERVER_5 As String
    Dim SERVER_6 As String
    Dim SERVER_7 As String
    Dim SERVER_8 As String




    Dim ctn As Integer
    Dim myResizer As New Resizer

    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        myResizer.FindAllControls(Me)
        load_csv_file()
        '-------------------------------------------------
        pingTimer.Interval = 1000
        pingTimer.Start()

        '-------------------------------------------------
        blinkTimer.Interval = 3000
        blinkTimer.Start()
    End Sub

    Private Sub pingTimer_Tick(sender As Object, e As EventArgs) Handles pingTimer.Tick
        CheckPing(Label1.Text, PictureBox1, ListBox1, GroupBox1.Text, TextBox1)
        CheckPing(Label2.Text, PictureBox2, ListBox2, GroupBox2.Text, TextBox2)
        CheckPing(Label3.Text, PictureBox3, ListBox3, GroupBox3.Text, TextBox3)
        CheckPing(Label4.Text, PictureBox4, ListBox4, GroupBox4.Text, TextBox4)
        CheckPing(Label5.Text, PictureBox5, ListBox5, GroupBox5.Text, TextBox5)
        CheckPing(Label6.Text, PictureBox6, ListBox6, GroupBox6.Text, TextBox6)
        CheckPing(Label8.Text, PictureBox7, ListBox7, GroupBox7.Text, TextBox7)
        CheckPing(Label9.Text, PictureBox8, ListBox8, GroupBox8.Text, TextBox8)
    End Sub

    Private Sub CheckPing(ByVal hostAddress As String, pictureBox As PictureBox, ByVal listbox As ListBox, ByVal servername As String, ByVal textbox As TextBox)
        Dim pingsender As New Ping()
        Dim timeout As Integer = 1000
        Static wasConnected As Boolean = False ' Track the previous state
        Dim soundPlayer As New SoundPlayer(Application.StartupPath & "\Sound.wav") ' Path to your sound file

        Try
List:
            Dim reply As PingReply = pingsender.Send(hostAddress, timeout)
            If reply.Status = IPStatus.Success Then
                pictureBox.Image = Image.FromFile(Application.StartupPath & "\ledgreen.png")
                listbox.Items.Add(Now.ToString & ": Connected")
                LogToCSV("Connected", hostAddress, servername)
                wasConnected = True
            Else
                pictureBox.Image = Image.FromFile(Application.StartupPath & "\ledred.png")
                listbox.Items.Add(Now.ToString & ": Disconnected")
                LogToCSV("Disconnected", hostAddress, servername)
                wasConnected = False
                soundPlayer.Play()

                If reply.Status.ToString = "11050" Then
                    textbox.Text = "Ping Transmit Error. General Failure.- " & hostAddress & ": " & Date.Now
                    soundPlayer.Play()
                ElseIf reply.Status.ToString = "TimedOut" Then
                    textbox.Text = "Request TimeOut.- " & hostAddress & ": " & Date.Now
                    soundPlayer.Play()
                End If
            End If

            If listbox.Items.Count > 3 Then
                listbox.Items.Clear()
                GoTo List
            End If
            listbox.SelectedIndex = listbox.Items.Count - 1

        Catch ex As Exception
            ' Handle exceptions if necessary
        End Try
    End Sub

    'Private Sub LogToCSV(ByVal status As String, ByVal hostAddress As String, ByVal servername As String)
    '    Dim logLine As String = String.Format("{0},{1},{2},{3}", Now.ToString("yyyy-MM-dd HH:mm:ss"), status, hostAddress, servername)
    '    Dim logFilePath As String = Application.StartupPath & "\ping_logs.csv"
    '    Using writer As New StreamWriter(logFilePath, True)
    '        writer.WriteLine(logLine)
    '    End Using
    'End Sub


    Private Sub LogToCSV(ByVal status As String, ByVal hostAddress As String, ByVal servername As String)
        Dim logLine As String = String.Format("{0},{1},{2},{3}", Now.ToString("yyyy-MM-dd HH:mm:ss"), status, hostAddress, servername)
        Dim baseDirectory As String = "C:\PingLogs"
        Dim serverDirectory As String = Path.Combine(baseDirectory, servername)

        ' Create the base directory if it doesn't exist
        If Not Directory.Exists(baseDirectory) Then
            Directory.CreateDirectory(baseDirectory)
        End If

        ' Create the server-specific directory if it doesn't exist
        If Not Directory.Exists(serverDirectory) Then
            Directory.CreateDirectory(serverDirectory)
        End If

        Dim logFilePath As String = Path.Combine(serverDirectory, "ping_logs_" & Now.ToString("yyyy-MM-dd") & ".csv")
        Using writer As New StreamWriter(logFilePath, True)
            writer.WriteLine(logLine)
        End Using
    End Sub

    Private Sub Timer2_Tick(sender As Object, e As EventArgs) Handles Timer2.Tick
        Label14.Text = DateTime.Now.ToString("MM-dd-yyyy HH:mm:ss")
    End Sub

    Public Shared Function ReadCSV(filePath As String) As Dictionary(Of String, String)
        Dim configValues As New Dictionary(Of String, String)
        If File.Exists(filePath) Then
            Dim lines As String() = File.ReadAllLines(filePath)
            For Each line As String In lines
                Dim parts As String() = line.Split(","c)
                If parts.Length = 2 Then
                    configValues(parts(0).Trim()) = parts(1).Trim()
                End If
            Next
        Else
            Console.WriteLine("File does not exist: " & filePath)
        End If
        Return configValues
    End Function

    Public Sub load_csv_file()
        Dim filePath As String = Application.StartupPath & "CONFIG.csv"
        Dim configValues As Dictionary(Of String, String) = Form1.ReadCSV(filePath)

        SERVER_1 = configValues("SERVER_1_NAME")
        SERVER_2 = configValues("SERVER_2_NAME")
        SERVER_3 = configValues("SERVER_3_NAME")
        SERVER_4 = configValues("SERVER_4_NAME")
        SERVER_5 = configValues("SERVER_5_NAME")
        SERVER_6 = configValues("SERVER_6_NAME")
        SERVER_7 = configValues("SERVER_7_NAME")
        SERVER_8 = configValues("SERVER_8_NAME")

        Label1.Text = configValues("SERVER_1")
        Label2.Text = configValues("SERVER_2")
        Label3.Text = configValues("SERVER_3")
        Label4.Text = configValues("SERVER_4")
        Label5.Text = configValues("SERVER_5")
        Label6.Text = configValues("SERVER_6")
        Label8.Text = configValues("SERVER_7")
        Label9.Text = configValues("SERVER_8")

        GroupBox1.Text = SERVER_1
        GroupBox2.Text = SERVER_2
        GroupBox3.Text = SERVER_3
        GroupBox4.Text = SERVER_4
        GroupBox5.Text = SERVER_5
        GroupBox6.Text = SERVER_6
        GroupBox7.Text = SERVER_7
        GroupBox8.Text = SERVER_8

    End Sub

    Private Sub Form1_Resize(sender As Object, e As EventArgs) Handles Me.Resize
        myResizer.ResizeAllControls(Me)
    End Sub

End Class