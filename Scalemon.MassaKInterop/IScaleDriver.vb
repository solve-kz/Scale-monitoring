
Public Interface IScaleDriver

        Sub OpenConnection()
        Sub CloseConnection()
        Sub SetToZero()
        Sub ReadWeight()

        Property PortConnection As String
        ReadOnly Property Weight As Decimal
        ReadOnly Property Stable As Boolean
        ReadOnly Property LastResponseNum As Long
        ReadOnly Property LastResponseText As String
        ReadOnly Property isConnected As Boolean
        ReadOnly Property isScaleAlarm As Boolean

    End Interface


