


Public Enum ArduinoSignalCode As Byte
    LinkOn = &H10 ' ���������� ��������� ����� � ������
    LinkOff = &H11 ' ����������� ��� ������ ����������
    Idle = &H12 ' ���������� ������� �����
    Unstuble = &H13 ' ����������� ������� � ������ �����
    Complited = &H14 ' ���������� ������ �����
    ComplitedSmall = &H15 ' ���������� ������ � ������� �����
    ScaleAlarm = &H16 ' ������� ����� ���������� �� 1 �������, ����� �����������
    SystemAlarm = &H17 ' ���������� ������� �����
    AlarmOff = &H18 ' ����������� ������� �����
End Enum

Public Enum ScalesState
    Disconnected       ' ���� �� ����������
    Connected          ' ����� ����������� ������������
    Unstable            ' ��� ����������������
    Stabilized          ' ��� �������������� � ������ ������������
    NegativeWeight          ' raw < 0
    ZeroWeight              ' raw == 0
    LightWeight             ' 0 < raw <= h
    InvalidWeight           ' h < raw <= minWeight
    ReadyToRecord           ' raw > minWeight � ���������� � ������
    ErrorAfterWeighing          ' > minWeight, �� ��� ���������������� ����
    Recorded                    ' ��� ������� � ��
    ScaleError          ' ���������� ������ �����
End Enum


Public Enum Trigger

    ScaleConnected       ' Scale.ConnectionEstablished
    ScaleDisconnected     ' Scale.ConnectionLost
    ScaleStabilized       ' ��� ������������ ������ ������������
    ReadinessToScale        ' ��� ���������� � �����������
    ResetToZero          '������������� ������ ���� � ����
    ScaleUnstable         ' Scale.Unstable
    ScaleAlarm            ' Scale.ScaleAlarm
    WeightReceived        ' ����� �������: �������� �������������� raw
    ArduinoButtonPressed  ' Arduino.ButtonPressed � ����� ��������� �����
    ' ���������� ������������ ��� ReadyToRecord
    RecordSuccess
    RecordFailure
End Enum

