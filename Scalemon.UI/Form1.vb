Public Class Form1
    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        lblHelpPath.Text = FormHelp.DescribeHelpLocation()
    End Sub

    Private Sub btnOpenHelp_Click(sender As Object, e As EventArgs) Handles btnOpenHelp.Click
        Try
            FormHelp.ShowHelp(Me)
        Catch ex As Exception
            MessageBox.Show(ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub
End Class
