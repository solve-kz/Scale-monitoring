<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class Form1
    Inherits System.Windows.Forms.Form

    'Форма переопределяет dispose для очистки списка компонентов.
    <System.Diagnostics.DebuggerNonUserCode()> _
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Является обязательной для конструктора форм Windows Forms
    Private components As System.ComponentModel.IContainer

    'Примечание: следующая процедура является обязательной для конструктора форм Windows Forms
    'Для ее изменения используйте конструктор форм Windows Form.
    'Не изменяйте ее в редакторе исходного кода.
    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        Me.btnOpenHelp = New System.Windows.Forms.Button()
        Me.lblHelpPath = New System.Windows.Forms.Label()
        Me.SuspendLayout()
        '
        'btnOpenHelp
        '
        Me.btnOpenHelp.Anchor = CType((System.Windows.Forms.AnchorStyles.Top Or System.Windows.Forms.AnchorStyles.Right), System.Windows.Forms.AnchorStyles)
        Me.btnOpenHelp.Location = New System.Drawing.Point(604, 12)
        Me.btnOpenHelp.Name = "btnOpenHelp"
        Me.btnOpenHelp.Size = New System.Drawing.Size(184, 29)
        Me.btnOpenHelp.TabIndex = 0
        Me.btnOpenHelp.Text = "Открыть справку"
        Me.btnOpenHelp.UseVisualStyleBackColor = True
        '
        'lblHelpPath
        '
        Me.lblHelpPath.Anchor = CType(((System.Windows.Forms.AnchorStyles.Top Or System.Windows.Forms.AnchorStyles.Left) _
                    Or System.Windows.Forms.AnchorStyles.Right), System.Windows.Forms.AnchorStyles)
        Me.lblHelpPath.AutoEllipsis = True
        Me.lblHelpPath.Location = New System.Drawing.Point(12, 12)
        Me.lblHelpPath.Name = "lblHelpPath"
        Me.lblHelpPath.Size = New System.Drawing.Size(586, 29)
        Me.lblHelpPath.TabIndex = 1
        Me.lblHelpPath.Text = "Определение пути справки..."
        Me.lblHelpPath.TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        '
        'Form1
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(8.0!, 20.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(800, 450)
        Me.Controls.Add(Me.lblHelpPath)
        Me.Controls.Add(Me.btnOpenHelp)
        Me.Name = "Form1"
        Me.Text = "Form1"
        Me.ResumeLayout(False)

    End Sub

    Friend WithEvents btnOpenHelp As Button
    Friend WithEvents lblHelpPath As Label
End Class
