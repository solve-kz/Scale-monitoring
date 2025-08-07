''' <summary>
''' ���������� �������������� �������� ��� ��������, �������� ������������������ � COM-������ ����� "�����-�".
''' </summary>
Public Interface IScaleDriver
    ''' <summary>
    ''' ��������� ���������� � COM-������.
    ''' </summary>
    Sub OpenConnection()

    ''' <summary>
    ''' ��������� ���������� � COM-������.
    ''' </summary>
    Sub CloseConnection()

    ''' <summary>
    ''' ���������� ������� ������ ���� �� ����.
    ''' </summary>
    Sub SetToZero()

    ''' <summary>
    ''' ���������� ������� ������� �������� ����.
    ''' </summary>
    Sub ReadWeight()

    ''' <summary>
    ''' ��� COM-����� ��� ����������� (��������, "COM1").
    ''' </summary>
    Property PortConnection As String

    ''' <summary>
    ''' ���������� ��������� ��������� �������� ����.
    ''' </summary>
    ReadOnly Property Weight As Decimal

    ''' <summary>
    ''' ���������� ����, �����������, �������� �� ��� ����������.
    ''' </summary>
    ReadOnly Property Stable As Boolean

    ''' <summary>
    ''' ���������� �������� ��� ���������� ������ �� �����.
    ''' </summary>
    ReadOnly Property LastResponseNum As Long

    ''' <summary>
    ''' ���������� ��������� �������� ���������� ������ �� �����.
    ''' </summary>
    ReadOnly Property LastResponseText As String

    ''' <summary>
    ''' ���������� ����, �����������, ����������� �� ���������� � ������.
    ''' </summary>
    ReadOnly Property isConnected As Boolean

    ''' <summary>
    ''' ���������� ����, ����������� �� ������� ���������� ������ �����.
    ''' </summary>
    ReadOnly Property isScaleAlarm As Boolean
End Interface