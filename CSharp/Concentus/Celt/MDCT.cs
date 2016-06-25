﻿/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2010 Xiph.Org Foundation
   Originally written by Jean-Marc Valin, Gregory Maxwell, and the Opus open-source contributors
   Ported to C# by Logan Stromberg

   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   - Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
   OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
   EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
   PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
   PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
   LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
   SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

namespace Concentus.Celt
{
    using Concentus.Celt.Enums;
    using Concentus.Celt.Structs;
    using Concentus.Common;
    using Concentus.Common.CPlusPlus;
    using System.Diagnostics;

    internal static class MDCT
    {
        /* Forward MDCT trashes the input array */
        internal static void clt_mdct_forward(MDCTLookup l, int[] input, int input_ptr, int[] output, int output_ptr,
            int[] window, int window_ptr, int overlap, int shift, int stride)
        {
            int i;
            int N, N2, N4;
            int[] f;
            int[] f2;
            FFTState st = l.kfft[shift];
            short[] trig;
            int trig_ptr = 0;
            int scale;
            
            int scale_shift = st.scale_shift - 1;
            scale = st.scale;

            N = l.n;
            trig = l.trig;
            for (i = 0; i < shift; i++)
            {
                N = N >> 1;
                trig_ptr += N;
            }
            N2 = N >> 1;
            N4 = N >> 2;

            f = new int[N2];
            f2 = new int[N4 * 2];

            /* Consider the input to be composed of four blocks: [a, b, c, d] */
            /* Window, shuffle, fold */
            {
                /* Temp pointers to make it really clear to the compiler what we're doing */
                int xp1 = input_ptr + (overlap >> 1);
                int xp2 = input_ptr + N2 - 1 + (overlap >> 1);
                int yp = 0;
                int wp1 = window_ptr + (overlap >> 1);
                int wp2 = window_ptr + ((overlap >> 1) - 1);
                for (i = 0; i < ((overlap + 3) >> 2); i++)
                {
                    /* Real part arranged as -d-cR, Imag part arranged as -b+aR*/
                    f[yp++] = Inlines.MULT16_32_Q15(window[wp2], input[xp1 + N2]) + Inlines.MULT16_32_Q15(window[wp1], input[xp2]);
                    f[yp++] = Inlines.MULT16_32_Q15(window[wp1], input[xp1]) - Inlines.MULT16_32_Q15(window[wp2], input[xp2 - N2]);
                    xp1 += 2;
                    xp2 -= 2;
                    wp1 += 2;
                    wp2 -= 2;
                }
                wp1 = window_ptr;
                wp2 = window_ptr + (overlap - 1);
                for (; i < N4 - ((overlap + 3) >> 2); i++)
                {
                    /* Real part arranged as a-bR, Imag part arranged as -c-dR */
                    f[yp++] = input[xp2];
                    f[yp++] = input[xp1];
                    xp1 += 2;
                    xp2 -= 2;
                }
                for (; i < N4; i++)
                {
                    /* Real part arranged as a-bR, Imag part arranged as -c-dR */
                    f[yp++] = Inlines.MULT16_32_Q15(window[wp2], input[xp2]) - Inlines.MULT16_32_Q15(window[wp1], input[xp1 - N2]);
                    f[yp++] = Inlines.MULT16_32_Q15(window[wp2], input[xp1]) + Inlines.MULT16_32_Q15(window[wp1], input[xp2 + N2]);
                    xp1 += 2;
                    xp2 -= 2;
                    wp1 += 2;
                    wp2 -= 2;
                }
            }
            /* Pre-rotation */
            {
                int yp = 0;
                int t = trig_ptr;
                for (i = 0; i < N4; i++)
                {
                    short t0, t1;
                    int re, im, yr, yi;
                    t0 = trig[t + i];
                    t1 = trig[t + N4 + i];
                    re = f[yp++];
                    im = f[yp++];
                    yr = KissFFT.S_MUL(re, t0) - KissFFT.S_MUL(im, t1);
                    yi = KissFFT.S_MUL(im, t0) + KissFFT.S_MUL(re, t1);
                    f2[2 * st.bitrev[i]] = Inlines.PSHR32(Inlines.MULT16_32_Q16(scale, yr), scale_shift);
                    f2[2 * st.bitrev[i] + 1] = Inlines.PSHR32(Inlines.MULT16_32_Q16(scale, yi), scale_shift);
                }
            }

            /* N/4 complex FFT, does not downscale anymore */
            KissFFT.opus_fft_impl(st, f2, 0);

            /* Post-rotate */
            {
                /* Temp pointers to make it really clear to the compiler what we're doing */
                int fp = 0;
                int yp1 = output_ptr;
                int yp2 = output_ptr + (stride * (N2 - 1));
                int t = trig_ptr;
                for (i = 0; i < N4; i++)
                {
                    int yr, yi;
                    yr = KissFFT.S_MUL(f2[fp + 1], trig[t + N4 + i]) - KissFFT.S_MUL(f2[fp], trig[t + i]);
                    yi = KissFFT.S_MUL(f2[fp], trig[t + N4 + i]) + KissFFT.S_MUL(f2[fp + 1], trig[t + i]);
                    output[yp1] = yr;
                    output[yp2] = yi;
                    fp += 2;
                    yp1 += (2 * stride);
                    yp2 -= (2 * stride);
                }
            }
        }

        internal static void clt_mdct_backward(MDCTLookup l, Pointer<int> input, Pointer<int> output,
              Pointer<int> window, int overlap, int shift, int stride)
        {
            int i;
            int N, N2, N4;
            Pointer<short> trig;

            N = l.n;
            trig = l.trig.GetPointer();
            for (i = 0; i < shift; i++)
            {
                N >>= 1;
                trig = trig.Point(N);
            }
            N2 = N >> 1;
            N4 = N >> 2;

            /* Pre-rotate */
            {
                /* Temp pointers to make it really clear to the compiler what we're doing */
                // FIXME: these can probably go away
                Pointer<int> xp1 = input;
                Pointer<int> xp2 = input.Point(stride * (N2 - 1));
                Pointer<int> yp = output.Point(overlap >> 1);
                Pointer<short> t = trig;
                Pointer<short> bitrev = l.kfft[shift].bitrev;
                for (i = 0; i < N4; i++)
                {
                    int rev;
                    int yr, yi;
                    rev = bitrev[0];
                    bitrev = bitrev.Point(1);
                    yr = KissFFT.S_MUL(xp2[0], t[i]) + KissFFT.S_MUL(xp1[0], t[N4 + i]);
                    yi = KissFFT.S_MUL(xp1[0], t[i]) - KissFFT.S_MUL(xp2[0], t[N4 + i]);
                    /* We swap real and imag because we use an FFT instead of an IFFT. */
                    yp[2 * rev + 1] = yr;
                    yp[2 * rev] = yi;
                    /* Storing the pre-rotation directly in the bitrev order. */
                    xp1 = xp1.Point(2 * stride);
                    xp2 = xp2.Point(0 - (2 * stride));
                }
            }
            
            KissFFT.opus_fft_impl(l.kfft[shift], output.Data, output.Offset + (overlap >> 1));

            /* Post-rotate and de-shuffle from both ends of the buffer at once to make
               it in-place. */
            {
                Pointer<int> yp0 = output.Point((overlap >> 1));
                Pointer<int> yp1 = output.Point((overlap >> 1) + N2 - 2);
                Pointer<short> t = trig;

                /* Loop to (N4+1)>>1 to handle odd N4. When N4 is odd, the
                   middle pair will be computed twice. */
                for (i = 0; i < (N4 + 1) >> 1; i++)
                {
                    int re, im, yr, yi;
                    short t0, t1;
                    /* We swap real and imag because we're using an FFT instead of an IFFT. */
                    re = yp0[1];
                    im = yp0[0];
                    t0 = t[i];
                    t1 = t[N4 + i];
                    /* We'd scale up by 2 here, but instead it's done when mixing the windows */
                    yr = KissFFT.S_MUL(re, t0) + KissFFT.S_MUL(im, t1);
                    yi = KissFFT.S_MUL(re, t1) - KissFFT.S_MUL(im, t0);
                    /* We swap real and imag because we're using an FFT instead of an IFFT. */
                    re = yp1[1];
                    im = yp1[0];
                    yp0[0] = yr;
                    yp1[1] = yi;

                    t0 = t[(N4 - i - 1)];
                    t1 = t[(N2 - i - 1)];
                    /* We'd scale up by 2 here, but instead it's done when mixing the windows */
                    yr = KissFFT.S_MUL(re, t0) + KissFFT.S_MUL(im, t1);
                    yi = KissFFT.S_MUL(re, t1) - KissFFT.S_MUL(im, t0);
                    yp1[0] = yr;
                    yp0[1] = yi;
                    yp0 = yp0.Point(2);
                    yp1 = yp1.Point(-2);
                }
            }

            /* Mirror on both sides for TDAC */
            {
                // fixme: remove these temps
                Pointer<int> xp1 = output.Point(overlap - 1);
                Pointer<int> yp1 = output;
                Pointer<int> wp1 = window;
                Pointer<int> wp2 = window.Point(overlap - 1);

                for (i = 0; i < overlap / 2; i++)
                {
                    int x1, x2;
                    x1 = xp1[0];
                    x2 = yp1[0];
                    yp1[0] = Inlines.MULT16_32_Q15(wp2[0], x2) - Inlines.MULT16_32_Q15(wp1[0], x1);
                    yp1 = yp1.Point(1);
                    xp1[0] = Inlines.MULT16_32_Q15(wp1[0], x2) + Inlines.MULT16_32_Q15(wp2[0], x1);
                    xp1 = xp1.Point(-1);
                    wp1 = wp1.Point(1);
                    wp2 = wp2.Point(-1);
                }
            }
        }
    }
}
