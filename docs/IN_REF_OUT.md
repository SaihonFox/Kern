# `in`, `ref`, `out` без новых ключевых слов

В Kern нет (и не планируется) ключевых слов `in`/`ref`/`out`: те же задачи решаются
существующими средствами языка. Ниже — рабочие шаблоны; они не меняют синтаксис и не
требуют атрибутов.

## 1. `in` — передача по значению только для чтения

Параметры Kern и так передаются по значению; `unit!` дополнительно неизменяем. Для
больших данных используйте `frozen_list`/`frozen_dict`/`frozen_set` (их нельзя изменить)
или `span<T>` для временного окна без копии.

```kern
unit! Point(i32 x, i32 y)

// Значение читается, но изменение копии не видно вызывающему.
fun distance2(Point p) -> i32 p.x * p.x + p.y * p.y

fun total(frozen_list<i32> xs) -> i32 {
	i32 sum = 0
	loop x in xs { sum += x }
	sum
}
```

Если нужен именно запрет записи на уровне API, принимайте `unit!`, `frozen_*` или
`span<T>` — компилятор не даст изменить такое значение.

## 2. `ref` — ссылочная семантика через `type`

`type` (класс) — ссылочный: изменения видны вызывающему. Это аналог `ref`-параметра,
только без нового ключевого слова.

```kern
type Counter {
	i32 value = 0
}

fun bump(Counter c) {
	c.value += 1
}

fun main {
	Counter c()
	bump(c)
	println(c.value) // 1
}
```

Для `unit` работает правило copy-out/copy-back (spec §9): запись в цепочку полей
возвращается в исходное значение.

```kern
unit Pair(i32 left, i32 right)

type Holder {
	Pair pair
}

fun move_right(Holder h) {
	h.pair.right += 1 // h.pair — unit: читается копия, затем пишется обратно
}
```

## 3. `out` — результат через возврат, кортеж или объект-приёмник

### Вариант A: `result` / кортеж (рекомендуется)

```kern
// bool — успех, i32 — значение (второй элемент осмыслен только при true).
fun try_parse(str text) -> (bool ok, i32 value) {
	match text.to_i32() {
		nil => (false, 0)
		var parsed => (true, parsed)
	}
}

fun main {
	var parsed = try_parse("42")
	if parsed.ok { println(parsed.value) }
}
```

### Вариант B: объект-приёмник (аналог `out`-параметра)

```kern
type Out<T> {
	T value
}
fun read_count(Out<i32> target) {
	target.value = 42
}

fun main {
	Out<i32> result()
	read_count(result)
	println(result.value)
}
```

### Вариант C: `result<T, E>` для ошибок

```kern
fun divide(i32 a, i32 b) -> result<i32, str> {
	if b == 0 { Err("деление на ноль") } else { Ok(a / b) }
}

fun main {
	match divide(10, 0) {
		Ok(value) => println(value)
		Err(message) => println(message)
	}
}
```

## 4. `ref` для нескольких значений: класс-контекст

```kern
type ParseState {
	str text
	i32 position
	bool ok
}

fun consume(ParseState state, char expected) {
	if state.position >= state.text.len || state.text[state.position] != expected {
		state.ok = false
		return
	}
	state.position += 1
}
```

## 5. Что выбрать

| Задача | Идиома Kern |
|---|---|
| только читать значение | обычный параметр (плюс `unit!`/`frozen_*`/`span<T>`) |
| изменить значение вызывающего | `type` (ссылка) |
| изменить поле `unit` | цепочка полей + copy-back (spec §9) |
| вернуть одно значение и признак успеха | `(bool, T)` или `result<T, E>` |
| «out»-параметр | `type Out<T> { T value }` |
| вернуть несколько значений | кортеж/именованный кортеж или `unit` |
