/*
 Name:		IndicationController.ino
 Created:	01.08.2025 1:12:26
 Author:	solve
*/

// Пины управления лампами
const byte PIN_LINK = 2;  // Зелёный индикатор "связь"
const byte PIN_GREEN = 3;  // Зелёный (Idle)
const byte PIN_YELLOW = 4;  // Желтый (Complited)
const byte PIN_RED = 5;  // Красный (ошибки)

// Пин кнопки
const byte PIN_BUTTON = 6;

// Последнее состояние кнопки
bool lastButtonState = HIGH;
unsigned long lastDebounceTime = 0;
const unsigned long debounceDelay = 20;  // антидребезг 20 мс

void setup() {
    // Настройка выходов
    pinMode(PIN_LINK, OUTPUT);
    pinMode(PIN_GREEN, OUTPUT);
    pinMode(PIN_YELLOW, OUTPUT);
    pinMode(PIN_RED, OUTPUT);

    // Настройка кнопки
    pinMode(PIN_BUTTON, INPUT_PULLUP);

    // Сброс всех индикаторов
    resetAllLamps();

    // Скорость порта
    Serial.begin(9600);
}

void loop() {
    // === 1. Обработка команд по UART ===
    if (Serial.available()) {
        byte cmd = Serial.read();
        handleCommand(cmd);
    }

    // === 2. Обработка кнопки ===
    bool buttonState = digitalRead(PIN_BUTTON);
    if (buttonState != lastButtonState) {
        lastDebounceTime = millis();
        lastButtonState = buttonState;
    }

    if ((millis() - lastDebounceTime) > debounceDelay && buttonState == LOW) {
        Serial.write(0x20);  // сигнал нажатой кнопки
        // Простой антизалип: ждём пока отпустят
        while (digitalRead(PIN_BUTTON) == LOW);
    }
}

// === Обработка команд ===
void handleCommand(byte cmd) {
    switch (cmd) {
    case 0x10: digitalWrite(PIN_LINK, HIGH); break;            // LinkOn
    case 0x11: resetAllLamps(); break;                          // LinkOff
    case 0x12: setOnly(PIN_GREEN); break;                       // Idle
    case 0x13: digitalWrite(PIN_GREEN, LOW); digitalWrite(PIN_YELLOW, LOW); break; // Unstuble
    case 0x14: setOnly(PIN_YELLOW); break;                      // Complited
    case 0x15: digitalWrite(PIN_YELLOW, HIGH); digitalWrite(PIN_RED, HIGH); break; // YellowRedOn
    case 0x16: setOnly(PIN_RED); break;                         // RedOn
    case 0x17: digitalWrite(PIN_RED, LOW); break;               // AlarmOff
    default: break;
    }
}

// === Вспомогательные ===
void resetAllLamps() {
    digitalWrite(PIN_LINK, LOW);
    digitalWrite(PIN_GREEN, LOW);
    digitalWrite(PIN_YELLOW, LOW);
    digitalWrite(PIN_RED, LOW);
}

void setOnly(byte pin) {
    digitalWrite(PIN_LINK, LOW);
    digitalWrite(PIN_GREEN, LOW);
    digitalWrite(PIN_YELLOW, LOW);
    digitalWrite(PIN_RED, LOW);
    digitalWrite(pin, HIGH);
}

