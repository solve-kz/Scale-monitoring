Imports System.IO
Imports System.Windows.Forms

''' <summary>
''' Определяет используемый файл справки и безопасно открывает его.
''' Сначала используется файл из папки программы, затем из %AppData%\OrderCreator.
''' </summary>
Public Class FormHelp
    Private Const HelpFileName As String = "help.chm"

    ''' <summary>
    ''' Возвращает путь к доступному файлу справки или выбрасывает подробное исключение.
    ''' </summary>
    Public Shared Function ResolveHelpPath() As String
        Dim appFolderHelp = Path.Combine(Application.StartupPath, HelpFileName)
        Dim roamingHelp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrderCreator", HelpFileName)

        If File.Exists(appFolderHelp) Then
            EnsureReadable(appFolderHelp)
            Return appFolderHelp
        End If

        If File.Exists(roamingHelp) Then
            EnsureReadable(roamingHelp)
            Return roamingHelp
        End If

        Throw New FileNotFoundException($"Файл справки {HelpFileName} не найден ни в папке программы, ни в %AppData%\\OrderCreator.")
    End Function

    ''' <summary>
    ''' Возвращает описание найденной справки или текст ошибки, если путь определить нельзя.
    ''' </summary>
    Public Shared Function DescribeHelpLocation() As String
        Try
            Dim path = ResolveHelpPath()
            Return $"Справка используется отсюда: {path}"
        Catch ex As Exception
            Return ex.Message
        End Try
    End Function

    ''' <summary>
    ''' Открывает файл справки, гарантируя, что он читается без ошибок доступа.
    ''' </summary>
    Public Shared Sub ShowHelp(owner As IWin32Window)
        Dim path = ResolveHelpPath()
        Help.ShowHelp(owner, path)
    End Sub

    Private Shared Sub EnsureReadable(path As String)
        Try
            Using File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            End Using
        Catch ex As UnauthorizedAccessException
            Throw New UnauthorizedAccessException($"Нет доступа к файлу справки: {path}", ex)
        End Try
    End Sub
End Class
