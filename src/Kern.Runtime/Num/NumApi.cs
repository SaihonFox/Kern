using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Num;

/// <summary>
/// <c>kern.num</c>: числа, векторы, матрицы, статистика, интерполяция, интегрирование,
/// поиск корней и FFT. Комплексное число — нативный тип <c>complex</c> (re/im, операторы,
/// <c>abs</c>, <c>conjugate</c>); матрица — <c>list&lt;list&lt;f64&gt;&gt;</c>, вектор —
/// <c>list&lt;f64&gt;</c>. FFT возвращает перемежающиеся re/im пары.
/// </summary>
[KernModule("kern.num")]
public static class NumApi
{
	/// <summary><c>mean(xs)</c>: среднее арифметическое.</summary>
	[KernMember]
	public static double Mean(KValue values)
	{
		double[] items = Doubles(values, "mean");
		if (items.Length == 0)
			throw new KernPanic("num.mean: пустой список");
		return items.Sum() / items.Length;
	}

	/// <summary><c>median(xs)</c>: медиана (среднее двух центральных для чётной длины).</summary>
	[KernMember]
	public static double Median(KValue values)
	{
		double[] items = Doubles(values, "median");
		if (items.Length == 0)
			throw new KernPanic("num.median: пустой список");
		Array.Sort(items);
		int middle = items.Length / 2;
		return items.Length % 2 == 1 ? items[middle] : (items[middle - 1] + items[middle]) / 2;
	}

	/// <summary><c>variance(xs)</c>: выборочная дисперсия.</summary>
	[KernMember]
	public static double Variance(KValue values) => Variance(values, sample: true);

	/// <summary><c>variance(xs, sample)</c>: дисперсия (sample=false — генеральная).</summary>
	[KernMember]
	public static double Variance(KValue values, bool sample)
	{
		double[] items = Doubles(values, "variance");
		int divisor = sample ? items.Length - 1 : items.Length;
		if (divisor <= 0)
			throw new KernPanic("num.variance: нужно минимум два значения (или одно при sample=false)");
		double mean = items.Sum() / items.Length;
		return items.Sum(item => (item - mean) * (item - mean)) / divisor;
	}

	/// <summary><c>stdev(xs)</c>: выборочное стандартное отклонение.</summary>
	[KernMember]
	public static double Stdev(KValue values) => System.Math.Sqrt(Variance(values));

	/// <summary><c>correlation(xs, ys)</c>: коэффициент корреляции Пирсона.</summary>
	[KernMember]
	public static double Correlation(KValue xs, KValue ys)
	{
		double[] x = Doubles(xs, "correlation");
		double[] y = Doubles(ys, "correlation");
		if (x.Length != y.Length || x.Length < 2)
			throw new KernPanic("num.correlation: списки должны быть одной длины (>= 2)");
		double mx = x.Average(), my = y.Average();
		double numerator = 0, dx = 0, dy = 0;
		for (int index = 0; index < x.Length; index++)
		{
			numerator += (x[index] - mx) * (y[index] - my);
			dx += (x[index] - mx) * (x[index] - mx);
			dy += (y[index] - my) * (y[index] - my);
		}
		return numerator / System.Math.Sqrt(dx * dy);
	}

	/// <summary><c>regression(xs, ys)</c>: <c>[наклон, сдвиг]</c> линейной регрессии.</summary>
	[KernMember]
	[KernReturn("list<f64>")]
	public static KValue Regression(KValue xs, KValue ys)
	{
		double[] x = Doubles(xs, "regression");
		double[] y = Doubles(ys, "regression");
		if (x.Length != y.Length || x.Length < 2)
			throw new KernPanic("num.regression: списки должны быть одной длины (>= 2)");
		double mx = x.Average(), my = y.Average();
		double numerator = 0, denominator = 0;
		for (int index = 0; index < x.Length; index++)
		{
			numerator += (x[index] - mx) * (y[index] - my);
			denominator += (x[index] - mx) * (x[index] - mx);
		}
		double slope = denominator == 0 ? 0 : numerator / denominator;
		double intercept = my - slope * mx;
		return Floats([slope, intercept]);
	}

	/// <summary><c>dot_product(a, b)</c>: скалярное произведение.</summary>
	[KernMember]
	[KernName("dot_product")]
	public static double Dot(KValue a, KValue b)
	{
		double[] x = Doubles(a, "dot_product");
		double[] y = Doubles(b, "dot_product");
		if (x.Length != y.Length)
			throw new KernPanic("num.dot_product: длины векторов должны совпадать");
		double sum = 0;
		for (int index = 0; index < x.Length; index++) sum += x[index] * y[index];
		return sum;
	}

	/// <summary><c>transpose(a)</c>: транспонирование матрицы.</summary>
	[KernMember]
	[KernReturn("list<list<f64>>")]
	public static KValue Transpose(KValue matrix)
	{
		double[][] source = Matrix(matrix, "transpose");
		double[][] result = new double[source[0].Length][];
		for (int column = 0; column < result.Length; column++)
		{
			result[column] = new double[source.Length];
			for (int row = 0; row < source.Length; row++)
				result[column][row] = source[row][column];
		}
		return MatrixValue(result);
	}

	/// <summary><c>matmul(a, b)</c>: произведение матриц.</summary>
	[KernName("matmul")]
	[KernMember]
	[KernReturn("list<list<f64>>")]
	public static KValue MatMul(KValue a, KValue b)
	{
		double[][] left = Matrix(a, "matmul");
		double[][] right = Matrix(b, "matmul");
		if (left[0].Length != right.Length)
			throw new KernPanic("num.matmul: число столбцов A должно равняться числу строк B");
		double[][] result = new double[left.Length][];
		for (int row = 0; row < left.Length; row++)
		{
			result[row] = new double[right[0].Length];
			for (int column = 0; column < right[0].Length; column++)
				for (int inner = 0; inner < right.Length; inner++)
					result[row][column] += left[row][inner] * right[inner][column];
		}
		return MatrixValue(result);
	}

	/// <summary><c>identity(n)</c>: единичная матрица n×n.</summary>
	[KernMember]
	[KernReturn("list<list<f64>>")]
	public static KValue Identity(int size)
	{
		if (size <= 0 || size > 1000)
			throw new KernPanic("num.identity: размер должен быть 1..1000");
		double[][] result = new double[size][];
		for (int row = 0; row < size; row++)
		{
			result[row] = new double[size];
			result[row][row] = 1;
		}
		return MatrixValue(result);
	}

	/// <summary><c>solve(a, b)</c>: решение системы A·x = b методом Гаусса.</summary>
	[KernMember]
	[KernReturn("list<f64>")]
	public static KValue Solve(KValue matrix, KValue vector)
	{
		double[][] a = Matrix(matrix, "solve");
		double[] b = Doubles(vector, "solve");
		int size = a.Length;
		if (b.Length != size || a[0].Length != size)
			throw new KernPanic("num.solve: ожидалась квадратная матрица n×n и вектор длины n");
		double[][] m = new double[size][];
		for (int row = 0; row < size; row++) m[row] = [.. a[row], b[row]];
		for (int pivot = 0; pivot < size; pivot++)
		{
			int best = pivot;
			for (int row = pivot + 1; row < size; row++)
				if (System.Math.Abs(m[row][pivot]) > System.Math.Abs(m[best][pivot])) best = row;
			if (System.Math.Abs(m[best][pivot]) < 1e-12)
				throw new KernPanic("num.solve: матрица вырождена");
			(m[pivot], m[best]) = (m[best], m[pivot]);
			double divisor = m[pivot][pivot];
			for (int column = pivot; column <= size; column++) m[pivot][column] /= divisor;
			for (int row = 0; row < size; row++)
			{
				if (row == pivot) continue;
				double factor = m[row][pivot];
				for (int column = pivot; column <= size; column++) m[row][column] -= factor * m[pivot][column];
			}
		}
		double[] x = new double[size];
		for (int row = 0; row < size; row++) x[row] = m[row][size];
		return Floats(x);
	}

	/// <summary><c>interpolate(xs, ys, x)</c>: линейная интерполяция (с зажимом краёв).</summary>
	[KernMember]
	public static double Interpolate(KValue xs, KValue ys, double x)
	{
		double[] px = Doubles(xs, "interpolate");
		double[] py = Doubles(ys, "interpolate");
		if (px.Length != py.Length || px.Length < 2)
			throw new KernPanic("num.interpolate: нужно минимум две точки одинаковой длины");
		if (x <= px[0]) return py[0];
		if (x >= px[^1]) return py[^1];
		int index = 0;
		while (index + 1 < px.Length && px[index + 1] < x) index++;
		double t = (x - px[index]) / (px[index + 1] - px[index]);
		return py[index] + t * (py[index + 1] - py[index]);
	}

	/// <summary><c>integrate(f, a, b)</c>: интеграл методом Симпсона (1000 шагов).</summary>
	[KernMember]
	public static double Integrate(Interpreter interpreter, KValue function, double a, double b) =>
		Integrate(interpreter, function, a, b, 1000);

	/// <summary><c>integrate(f, a, b, steps)</c>: интеграл методом Симпсона.</summary>
	[KernMember]
	public static double Integrate(Interpreter interpreter, KValue function, double a, double b, int steps)
	{
		if (steps <= 0 || steps > 1_000_000)
			throw new KernPanic("num.integrate: число шагов должно быть 1..1 000 000");
		if (steps % 2 == 1) steps++;
		double h = (b - a) / steps;
		double sum = Call(interpreter, function, a) + Call(interpreter, function, b);
		for (int index = 1; index < steps; index++)
			sum += (index % 2 == 0 ? 2 : 4) * Call(interpreter, function, a + index * h);
		return sum * h / 3;
	}

	/// <summary><c>root(f, a, b)</c>: корень на отрезке методом бисекции (100 итераций).</summary>
	[KernMember]
	public static double Root(Interpreter interpreter, KValue function, double a, double b) =>
		Root(interpreter, function, a, b, 100);

	/// <summary><c>root(f, a, b, iterations)</c>: корень методом бисекции.</summary>
	[KernMember]
	public static double Root(Interpreter interpreter, KValue function, double a, double b, int iterations)
	{
		if (iterations <= 0 || iterations > 10_000)
			throw new KernPanic("num.root: число итераций должно быть 1..10 000");
		double fa = Call(interpreter, function, a);
		double fb = Call(interpreter, function, b);
		if (fa == 0) return a;
		if (fb == 0) return b;
		if (fa * fb > 0)
			throw new KernPanic("num.root: на концах отрезка функция одного знака");
		for (int index = 0; index < iterations; index++)
		{
			double middle = (a + b) / 2;
			double fm = Call(interpreter, function, middle);
			if (fm == 0 || (b - a) / 2 < 1e-12) return middle;
			if (fa * fm < 0) { b = middle; fb = fm; }
			else { a = middle; fa = fm; }
		}
		return (a + b) / 2;
	}

	/// <summary><c>fft(xs)</c>: дискретное преобразование Фурье; результат — re, im, re, im…</summary>
	[KernMember]
	[KernReturn("list<f64>")]
	public static KValue Fft(KValue values)
	{
		double[] input = Doubles(values, "fft");
		int size = 1;
		while (size < input.Length) size <<= 1;
		double[] re = new double[size];
		double[] im = new double[size];
		Array.Copy(input, re, input.Length);
		Fft(re, im);
		double[] result = new double[size * 2];
		for (int index = 0; index < size; index++)
		{
			result[index * 2] = re[index];
			result[index * 2 + 1] = im[index];
		}
		return Floats(result);
	}

	private static void Fft(double[] re, double[] im)
	{
		int size = re.Length;
		for (int i = 1, j = 0; i < size; i++)
		{
			int bit = size >> 1;
			for (; (j & bit) != 0; bit >>= 1) j ^= bit;
			j ^= bit;
			if (i < j)
			{
				(re[i], re[j]) = (re[j], re[i]);
				(im[i], im[j]) = (im[j], im[i]);
			}
		}
		for (int length = 2; length <= size; length <<= 1)
		{
			double angle = -2 * System.Math.PI / length;
			double wr = System.Math.Cos(angle), wi = System.Math.Sin(angle);
			for (int start = 0; start < size; start += length)
			{
				double cr = 1, ci = 0;
				for (int offset = 0; offset < length / 2; offset++)
				{
					int left = start + offset, right = left + length / 2;
					double tr = re[right] * cr - im[right] * ci;
					double ti = re[right] * ci + im[right] * cr;
					re[right] = re[left] - tr;
					im[right] = im[left] - ti;
					re[left] += tr;
					im[left] += ti;
					(cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
				}
			}
		}
	}

	private static double Call(Interpreter interpreter, KValue function, double x) =>
		interpreter.RunValue(function, KValue.F64(x)).AsF64;

	private static double[] Doubles(KValue value, string name)
	{
		if (value.Ref is not KList list)
			throw new KernPanic($"num.{name}: ожидался list<f64>, получено {value.TypeName}");
		double[] result = new double[list.Count];
		for (int index = 0; index < result.Length; index++)
		{
			KValue item = list[index];
			if (item.Kind is not (ValueKind.F64 or ValueKind.F32 or ValueKind.F16 or
				ValueKind.I8 or ValueKind.I16 or ValueKind.I32 or ValueKind.I64 or
				ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64))
				throw new KernPanic($"num.{name}: элемент {index} — {item.TypeName}, ожидалось число");
			result[index] = item.AsF64;
		}
		return result;
	}

	private static double[][] Matrix(KValue value, string name)
	{
		if (value.Ref is not KList list || list.Count == 0)
			throw new KernPanic($"num.{name}: ожидалась непустая матрица list<list<f64>>");
		double[][] result = new double[list.Count][];
		for (int index = 0; index < result.Length; index++)
			result[index] = Doubles(list[index], name);
		int width = result[0].Length;
		if (result.Any(row => row.Length != width))
			throw new KernPanic($"num.{name}: строки матрицы разной длины");
		return result;
	}

	private static KValue Floats(IEnumerable<double> values) =>
		KValue.Obj(new KList(null, values.Select(KValue.F64)));

	private static KValue MatrixValue(double[][] values) =>
		KValue.Obj(new KList(null, values.Select(row => Floats(row))));
}

/// <summary>Комплексное число <c>complex</c>.</summary>
[KernType("complex")]
public sealed class KernComplex(double re, double im)
{
	public double Re { get; } = re;
	public double Im { get; } = im;
}

/// <summary>Конструктор и операторы <c>complex</c>.</summary>
[KernType("complex")]
public static class ComplexApi
{
	/// <summary><c>complex(re, im)</c>.</summary>
	[KernCtor]
	[KernName("complex")]
	public static KernComplex Create(double re, double im) => new(re, im);

	/// <summary><c>z.re</c>.</summary>
	[KernProperty]
	public static double Re(KNativeBox self) => Require(self).Re;

	/// <summary><c>z.im</c>.</summary>
	[KernProperty]
	public static double Im(KNativeBox self) => Require(self).Im;

	/// <summary><c>z.abs()</c>: модуль.</summary>
	[KernMember]
	public static double Abs(KNativeBox self) =>
		System.Math.Sqrt(Require(self).Re * Require(self).Re + Require(self).Im * Require(self).Im);

	/// <summary><c>z.conjugate()</c>: сопряжённое.</summary>
	[KernMember]
	public static KernComplex Conjugate(KNativeBox self) => new(Require(self).Re, -Require(self).Im);

	/// <summary><c>a + b</c>.</summary>
	[KernMember(Operator = "+")]
	public static KernComplex Add(KNativeBox self, KernComplex other) =>
		new(Require(self).Re + other.Re, Require(self).Im + other.Im);

	/// <summary><c>a - b</c>.</summary>
	[KernMember(Operator = "-")]
	public static KernComplex Subtract(KNativeBox self, KernComplex other) =>
		new(Require(self).Re - other.Re, Require(self).Im - other.Im);

	/// <summary><c>a * b</c>.</summary>
	[KernMember(Operator = "*")]
	public static KernComplex Multiply(KNativeBox self, KernComplex other)
	{
		KernComplex a = Require(self);
		return new(a.Re * other.Re - a.Im * other.Im, a.Re * other.Im + a.Im * other.Re);
	}

	/// <summary><c>a / b</c>.</summary>
	[KernMember(Operator = "/")]
	public static KernComplex Divide(KNativeBox self, KernComplex other)
	{
		KernComplex a = Require(self);
		double denominator = other.Re * other.Re + other.Im * other.Im;
		if (denominator == 0)
			throw new KernPanic("complex: деление на ноль");
		return new((a.Re * other.Re + a.Im * other.Im) / denominator,
			(a.Im * other.Re - a.Re * other.Im) / denominator);
	}

	/// <summary><c>z.to_str()</c>: <c>re+im i</c>.</summary>
	[KernMember]
	[KernName("to_str")]
	public static string ToStr(KNativeBox self)
	{
		KernComplex value = Require(self);
		string sign = value.Im < 0 ? "-" : "+";
		return $"{value.Re}{sign}{System.Math.Abs(value.Im)}i";
	}

	private static KernComplex Require(KNativeBox self) => self.Value switch
	{
		KernComplex value => value,
		_ => throw new KernPanic($"ожидался complex, получено {self.Value.GetType().Name}"),
	};
}
