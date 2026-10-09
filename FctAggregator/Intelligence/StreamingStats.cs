using System.Text.Json;

namespace FctAggregator;

/// <summary>正常态底座：Welford 在线均值/样本σ（O(1)/样本，可跨重启持久化）。</summary>
public sealed class Welford
{
    public long N { get; private set; }
    public double Mean { get; private set; }
    private double _m2;

    public void Update(double x)
    {
        N++;
        var d = x - Mean;
        Mean += d / N;
        _m2 += d * (x - Mean);
    }

    /// <summary>样本标准差（n-1 口径），n&lt;2 时返回 0。
    /// _m2 是浮点累加量，可能因误差成为极小负数 → Sqrt(负数)=NaN，而 NaN 参与比较全为 false，
    /// 会让该测项静默不报警。故 m2 &lt;= 0 一律按 0 处理。</summary>
    public double Sigma => N < 2 || _m2 <= 0 ? 0 : Math.Sqrt(_m2 / (N - 1));

    public (long n, double mean, double m2) Snapshot() => (N, Mean, _m2);
    public void Restore(long n, double mean, double m2) { N = n; Mean = mean; _m2 = m2; }
}

/// <summary>正常态底座：P² 流式分位数（Jain-Chlamtac 5 标记法，O(1) 内存，状态可序列化）。</summary>
public sealed class P2Quantile
{
    private const double Eps = 1e-12;
    private readonly double _p;
    private long _count;
    private readonly double[] _q = new double[5];   // 标记高度
    private readonly double[] _npos = new double[5]; // 实际位置（1 基）
    private bool _initialized;

    public P2Quantile(double p) => _p = Math.Clamp(p, 0.0, 1.0);

    public void Update(double x)
    {
        if (!_initialized)
        {
            // 前 5 个样本排序填充
            int i = (int)_count;
            _q[i] = x;
            _count++;
            if (_count == 5)
            {
                Array.Sort(_q);
                for (int k = 0; k < 5; k++) _npos[k] = k + 1;
                _initialized = true;
            }
            return;
        }

        _count++;
        int cell;
        if (x < _q[0]) { _q[0] = x; cell = 0; }
        else if (x >= _q[4]) { _q[4] = x; cell = 3; }
        else
        {
            cell = 0;
            while (cell < 4 && _q[cell + 1] <= x) cell++;
            // 落在 [cell, cell+1) 之间：仅严格高于样本位置的标记（cell+1..4）右移（与论文一致）
        }
        for (int i = cell + 1; i < 5; i++) _npos[i]++;

        // 期望位置 = 1 + (n-1)*i/4（1 基）
        double dn = (_count - 1) / 4.0;
        for (int i = 0; i < 5; i++)
        {
            double np = 1 + dn * i;
            double d = np - _npos[i];
            bool adjust = (d >= 1 && _npos[i + 1] - _npos[i] > 1) || (d <= -1 && _npos[i - 1] - _npos[i] < -1);
            if (!adjust) continue;
            int sign = d >= 0 ? 1 : -1;
            double q = Parabolic(i, sign);
            if (_q[i - 1] < q && q < _q[i + 1]) _q[i] = q;
            else _q[i] = Linear(i, sign);
            _npos[i] += sign;
        }
    }

    public double Quantile()
    {
        if (_count == 0) return 0;
        if (!_initialized)
        {
            // 样本数 <5：直接取已收样本的次序统计量
            int n = (int)_count;
            var s = _q.Take(n).OrderBy(v => v).ToList();
            int idx = (int)Math.Round(_p * (n - 1));
            return s[Math.Clamp(idx, 0, n - 1)];
        }
        // 线性插值到精确分位位置
        double target = 1 + (_count - 1) * _p;
        int i = 0;
        while (i < 4 && _npos[i + 1] < target) i++;
        if (_npos[i + 1] - _npos[i] < Eps) return _q[i];
        double t = (target - _npos[i]) / (_npos[i + 1] - _npos[i]);
        return _q[i] + t * (_q[i + 1] - _q[i]);
    }

    private double Parabolic(int i, int sign)
    {
        // 论文原式：q'[i] = q[i] + s/(npos[i+1]-npos[i-1]) * [ (npos[i]-npos[i-1]+s)·(q[i+1]-q[i])/(npos[i+1]-npos[i])
        //                                              + (npos[i+1]-npos[i]-s)·(q[i]-q[i-1])/(npos[i]-npos[i-1]) ]
        double d1 = _npos[i] - _npos[i - 1];
        double d2 = _npos[i + 1] - _npos[i];
        double den = _npos[i + 1] - _npos[i - 1];
        if (den < Eps || d1 < Eps || d2 < Eps) return _q[i];
        double delta = sign / den * ((d1 + sign) * (_q[i + 1] - _q[i]) / d2 + (d2 - sign) * (_q[i] - _q[i - 1]) / d1);
        return double.IsFinite(delta) ? _q[i] + delta : _q[i];
    }

    private double Linear(int i, int sign)
    {
        double a = _q[i] + sign * (_q[i + sign] - _q[i]) / (_npos[i + sign] - _npos[i]);
        return double.IsFinite(a) ? a : _q[i];
    }

    private sealed class State
    {
        public double p { get; set; }
        public long n { get; set; }
        public double[] q { get; set; } = new double[5];
        public double[] pos { get; set; } = new double[5];
        public bool init { get; set; }
    }

    public string Serialize() => JsonSerializer.Serialize(new State { p = _p, n = _count, q = _q, pos = _npos, init = _initialized });

    public static P2Quantile? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<State>(json);
            if (s == null || s.q.Length != 5 || s.pos.Length != 5) return null;
            var r = new P2Quantile(s.p) { _count = s.n, _initialized = s.init };
            Array.Copy(s.q, r._q, 5);
            Array.Copy(s.pos, r._npos, 5);
            return r;
        }
        catch { return null; }
    }
}
