using UnityEngine;

namespace VRL
{
    // CPU 側の手続き的な形づくり(岩・地形)用の 3D グラディエントノイズ。シェーダー側の VrlNoise.hlsl とは別物(値は一致しない)。
    public static class VrlNoise
    {
        static int Hash(int x, int y, int z)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + z * 1274126177;
                h = (h ^ (h >> 13)) * 1274126177;
                return h ^ (h >> 16);
            }
        }

        static float Grad(int h, float x, float y, float z)
        {
            switch (h & 15)
            {
                case 0: return x + y; case 1: return -x + y; case 2: return x - y; case 3: return -x - y;
                case 4: return x + z; case 5: return -x + z; case 6: return x - z; case 7: return -x - z;
                case 8: return y + z; case 9: return -y + z; case 10: return y - z; case 11: return -y - z;
                case 12: return x + y; case 13: return -x + y; case 14: return -y + z; default: return -y - z;
            }
        }

        static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

        /// <summary>おおよそ -1〜1。</summary>
        public static float Gradient(Vector3 p)
        {
            int xi = Mathf.FloorToInt(p.x), yi = Mathf.FloorToInt(p.y), zi = Mathf.FloorToInt(p.z);
            float xf = p.x - xi, yf = p.y - yi, zf = p.z - zi;
            float u = Fade(xf), v = Fade(yf), w = Fade(zf);
            float x00 = Mathf.Lerp(Grad(Hash(xi, yi, zi), xf, yf, zf), Grad(Hash(xi + 1, yi, zi), xf - 1, yf, zf), u);
            float x10 = Mathf.Lerp(Grad(Hash(xi, yi + 1, zi), xf, yf - 1, zf), Grad(Hash(xi + 1, yi + 1, zi), xf - 1, yf - 1, zf), u);
            float x01 = Mathf.Lerp(Grad(Hash(xi, yi, zi + 1), xf, yf, zf - 1), Grad(Hash(xi + 1, yi, zi + 1), xf - 1, yf, zf - 1), u);
            float x11 = Mathf.Lerp(Grad(Hash(xi, yi + 1, zi + 1), xf, yf - 1, zf - 1), Grad(Hash(xi + 1, yi + 1, zi + 1), xf - 1, yf - 1, zf - 1), u);
            return Mathf.Lerp(Mathf.Lerp(x00, x10, v), Mathf.Lerp(x01, x11, v), w);
        }

        public static float Fbm(Vector3 p, int octaves, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0, amp = 0.5f;
            for (int i = 0; i < octaves; i++)
            {
                sum += Gradient(p) * amp;
                p = p * lacunarity + new Vector3(17.3f, 9.1f, 4.7f);
                amp *= gain;
            }
            return sum;
        }

        /// <summary>尾根の立つノイズ(0〜1 くらい)。</summary>
        public static float Ridged(Vector3 p, int octaves)
        {
            float sum = 0, amp = 0.5f;
            for (int i = 0; i < octaves; i++)
            {
                float n = 1f - Mathf.Abs(Gradient(p));
                sum += n * n * amp;
                p = p * 2.03f + new Vector3(3.1f, 7.7f, 1.3f);
                amp *= 0.5f;
            }
            return sum;
        }
    }
}
