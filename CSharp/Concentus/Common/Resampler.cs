﻿/* Copyright (C) 2007-2008 Jean-Marc Valin
   Copyright (C) 2008      Thorvald Natvig
   Ported to C# by Logan Stromberg
      
   File: Resampler.cs
   Arbitrary resampling code

   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions are
   met:

   1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.

   2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   3. The name of the author may not be used to endorse or promote products
   derived from this software without specific prior written permission.

   THIS SOFTWARE IS PROVIDED BY THE AUTHOR ``AS IS'' AND ANY EXPRESS OR
   IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
   OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
   DISCLAIMED. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY DIRECT,
   INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
   (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
   SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
   HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
   STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
   ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
   POSSIBILITY OF SUCH DAMAGE.
*/

/*
   The design goals of this code are:
      - Very fast algorithm
      - SIMD-friendly algorithm
      - Low memory requirement
      - Good *perceptual* quality (and not best SNR)

   Warning: This resampler is relatively new. Although I think I got rid of 
   all the major bugs and I don't expect the API to change anymore, there
   may be something I've missed. So use with caution.

   This algorithm is based on this original resampling algorithm:
   Smith, Julius O. Digital Audio Resampling Home Page
   Center for Computer Research in Music and Acoustics (CCRMA), 
   Stanford University, 2007.
   Web published at http://www-ccrma.stanford.edu/~jos/resample/.

   There is one main difference, though. This resampler uses cubic 
   interpolation instead of linear interpolation in the above paper. This
   makes the table much smaller and makes it possible to compute that table
   on a per-stream basis. In turn, being able to tweak the table for each 
   stream makes it possible to both reduce complexity on simple ratios 
   (e.g. 2/3), and get rid of the rounding operations in the inner loop. 
   The latter both reduces CPU time and makes the algorithm more SIMD-friendly.
*/

using Concentus.Celt;
using Concentus.Common.CPlusPlus;
using System;

namespace Concentus.Common
{
    public class SpeexResampler
    {
        private const int FIXED_STACK_ALLOC = 8192;

        #region Encoder state

        private int in_rate = 0;
        private int out_rate = 0;
        private int num_rate = 0;
        private int den_rate = 0;

        private int quality = 0;
        private int nb_channels = 0;
        private int filt_len = 0;
        private int mem_alloc_size = 0;
        private int buffer_size = 0;
        private int int_advance = 0;
        private int frac_advance = 0;
        private float cutoff = 0;
        private int oversample = 0;
        private int initialised = 0;
        private int started = 0;

        /* These are per-channel */
        private int[] last_sample = null;
        private int[] samp_frac_num = null;
        private int[] magic_samples = null;

        private short[] mem = null;
        private short[] sinc_table = null;
        private int sinc_table_length = 0;
        private resampler_basic_func resampler_ptr = null;

        int in_stride = 0;
        int out_stride = 0;

        /// <summary>
        /// Create() is the only way to make a resampler instance publically
        /// </summary>
        private SpeexResampler() { }

        #endregion
        
        #region Helper classes and tables

        private class FuncDef
        {
            public FuncDef(double[] t, int os)
            {
                table = t;
                oversample = os;
            }

            public double[] table;
            public int oversample;
            
            public static readonly double[] kaiser12_table/*[68]*/ = {
                0.99859849, 1.00000000, 0.99859849, 0.99440475, 0.98745105, 0.97779076,
                0.96549770, 0.95066529, 0.93340547, 0.91384741, 0.89213598, 0.86843014,
                0.84290116, 0.81573067, 0.78710866, 0.75723148, 0.72629970, 0.69451601,
                0.66208321, 0.62920216, 0.59606986, 0.56287762, 0.52980938, 0.49704014,
                0.46473455, 0.43304576, 0.40211431, 0.37206735, 0.34301800, 0.31506490,
                0.28829195, 0.26276832, 0.23854851, 0.21567274, 0.19416736, 0.17404546,
                0.15530766, 0.13794294, 0.12192957, 0.10723616, 0.09382272, 0.08164178,
                0.07063950, 0.06075685, 0.05193064, 0.04409466, 0.03718069, 0.03111947,
                0.02584161, 0.02127838, 0.01736250, 0.01402878, 0.01121463, 0.00886058,
                0.00691064, 0.00531256, 0.00401805, 0.00298291, 0.00216702, 0.00153438,
                0.00105297, 0.00069463, 0.00043489, 0.00025272, 0.00013031, 0.0000527734,
                0.00001000, 0.00000000};
            /*
            static double kaiser12_table[36] = {
            0.99440475, 1.00000000, 0.99440475, 0.97779076, 0.95066529, 0.91384741,
            0.86843014, 0.81573067, 0.75723148, 0.69451601, 0.62920216, 0.56287762,
            0.49704014, 0.43304576, 0.37206735, 0.31506490, 0.26276832, 0.21567274,
            0.17404546, 0.13794294, 0.10723616, 0.08164178, 0.06075685, 0.04409466,
            0.03111947, 0.02127838, 0.01402878, 0.00886058, 0.00531256, 0.00298291,
            0.00153438, 0.00069463, 0.00025272, 0.0000527734, 0.00000500, 0.00000000};
            */
            public static readonly double[] kaiser10_table/*[36]*/ = {
                0.99537781, 1.00000000, 0.99537781, 0.98162644, 0.95908712, 0.92831446,
                0.89005583, 0.84522401, 0.79486424, 0.74011713, 0.68217934, 0.62226347,
                0.56155915, 0.50119680, 0.44221549, 0.38553619, 0.33194107, 0.28205962,
                0.23636152, 0.19515633, 0.15859932, 0.12670280, 0.09935205, 0.07632451,
                0.05731132, 0.04193980, 0.02979584, 0.02044510, 0.01345224, 0.00839739,
                0.00488951, 0.00257636, 0.00115101, 0.00035515, 0.00000000, 0.00000000};

            public static readonly double[] kaiser8_table/*[36]*/ = {
                0.99635258, 1.00000000, 0.99635258, 0.98548012, 0.96759014, 0.94302200,
                0.91223751, 0.87580811, 0.83439927, 0.78875245, 0.73966538, 0.68797126,
                0.63451750, 0.58014482, 0.52566725, 0.47185369, 0.41941150, 0.36897272,
                0.32108304, 0.27619388, 0.23465776, 0.19672670, 0.16255380, 0.13219758,
                0.10562887, 0.08273982, 0.06335451, 0.04724088, 0.03412321, 0.02369490,
                0.01563093, 0.00959968, 0.00527363, 0.00233883, 0.00050000, 0.00000000};

            public static readonly double[] kaiser6_table/*[36]*/ = {
                0.99733006, 1.00000000, 0.99733006, 0.98935595, 0.97618418, 0.95799003,
                0.93501423, 0.90755855, 0.87598009, 0.84068475, 0.80211977, 0.76076565,
                0.71712752, 0.67172623, 0.62508937, 0.57774224, 0.53019925, 0.48295561,
                0.43647969, 0.39120616, 0.34752997, 0.30580127, 0.26632152, 0.22934058,
                0.19505503, 0.16360756, 0.13508755, 0.10953262, 0.08693120, 0.06722600,
                0.05031820, 0.03607231, 0.02432151, 0.01487334, 0.00752000, 0.00000000};
        };

        private class QualityMapping
        {
            public int base_length = 0;
            public int oversample = 0;
            public float downsample_bandwidth = 0;
            public float upsample_bandwidth = 0;
            public FuncDef window_func = null;

            private QualityMapping(int bl, int os, float dsb, float usb, FuncDef wf)
            {
                base_length = bl;
                oversample = os;
                downsample_bandwidth = dsb;
                upsample_bandwidth = usb;
                window_func = wf;
            }

            /* This table maps conversion quality to private parameters. There are two
               reasons that explain why the up-sampling bandwidth is larger than the 
               down-sampling bandwidth:
               1) When up-sampling, we can assume that the spectrum is already attenuated
                  close to the Nyquist rate (from an A/D or a previous resampling filter)
               2) Any aliasing that occurs very close to the Nyquist rate will be masked
                  by the sinusoids/noise just below the Nyquist rate (guaranteed only for
                  up-sampling).
            */
            public static readonly QualityMapping[] quality_map = {
                new QualityMapping(  8,  4, 0.830f, 0.860f, new FuncDef(FuncDef.kaiser6_table, 32) ), /* Q0 */
                new QualityMapping( 16,  4, 0.850f, 0.880f, new FuncDef(FuncDef.kaiser6_table, 32) ), /* Q1 */
                new QualityMapping( 32,  4, 0.882f, 0.910f, new FuncDef(FuncDef.kaiser6_table, 32) ), /* Q2 */  /* 82.3% cutoff ( ~60 dB stop) 6  */
                new QualityMapping( 48,  8, 0.895f, 0.917f, new FuncDef(FuncDef.kaiser8_table, 32) ), /* Q3 */  /* 84.9% cutoff ( ~80 dB stop) 8  */
                new QualityMapping( 64,  8, 0.921f, 0.940f, new FuncDef(FuncDef.kaiser8_table, 32) ), /* Q4 */  /* 88.7% cutoff ( ~80 dB stop) 8  */
                new QualityMapping( 80, 16, 0.922f, 0.940f, new FuncDef(FuncDef.kaiser10_table, 32)), /* Q5 */  /* 89.1% cutoff (~100 dB stop) 10 */
                new QualityMapping( 96, 16, 0.940f, 0.945f, new FuncDef(FuncDef.kaiser10_table, 32)), /* Q6 */  /* 91.5% cutoff (~100 dB stop) 10 */
                new QualityMapping(128, 16, 0.950f, 0.950f, new FuncDef(FuncDef.kaiser10_table, 32)), /* Q7 */  /* 93.1% cutoff (~100 dB stop) 10 */
                new QualityMapping(160, 16, 0.960f, 0.960f, new FuncDef(FuncDef.kaiser10_table, 32)), /* Q8 */  /* 94.5% cutoff (~100 dB stop) 10 */
                new QualityMapping(192, 32, 0.968f, 0.968f, new FuncDef(FuncDef.kaiser12_table, 64)), /* Q9 */  /* 95.5% cutoff (~100 dB stop) 10 */
                new QualityMapping(256, 32, 0.975f, 0.975f, new FuncDef(FuncDef.kaiser12_table, 64)), /* Q10 */ /* 96.6% cutoff (~100 dB stop) 10 */
            };
        }

        #endregion

        #region Private code

        /// <summary>
        /// typedef int (* resampler_basic_func)(SpeexResamplerState*, int , Pointer<short>, int *, Pointer<short>, Pointer<int>);
        /// </summary>
        private delegate int resampler_basic_func(int channel_index, Pointer<short> input, ref int in_len, Pointer<short> output, ref int out_len);

        private static short WORD2INT(float x)
        {
            return x < short.MinValue ? short.MinValue : (x > short.MaxValue ? short.MaxValue : (short)x);
        }

        /*8,24,40,56,80,104,128,160,200,256,320*/
        private static double compute_func(float x, FuncDef func)
        {
            float y, frac;
            double[] interp = new double[4];
            int ind;
            y = x * func.oversample;
            ind = (int)Math.Floor(y);
            frac = (y - ind);
            /* CSE with handle the repeated powers */
            interp[3] = -0.1666666667 * frac + 0.1666666667 * (frac * frac * frac);
            interp[2] = frac + 0.5 * (frac * frac) - 0.5 * (frac * frac * frac);
            /*interp[2] = 1.f - 0.5f*frac - frac*frac + 0.5f*frac*frac*frac;*/
            interp[0] = -0.3333333333 * frac + 0.5 * (frac * frac) - 0.1666666667 * (frac * frac * frac);
            /* Just to make sure we don't have rounding problems */
            interp[1] = 1.0f - interp[3] - interp[2] - interp[0];

            /*sum = frac*accum[1] + (1-frac)*accum[2];*/
            return interp[0] * func.table[ind] + interp[1] * func.table[ind + 1] + interp[2] * func.table[ind + 2] + interp[3] * func.table[ind + 3];
        }

        /* The slow way of computing a sinc for the table. Should improve that some day */
        private static short sinc(float cutoff, float x, int N, FuncDef window_func)
        {
            /*fprintf (stderr, "%f ", x);*/
            float xx = x * cutoff;
            if (Math.Abs(x) < 1e-6f)
                return WORD2INT(32768.0f * cutoff);
            else if (Math.Abs(x) > .5f * N)
                return 0;
            /*FIXME: Can it really be any slower than this? */
            return WORD2INT(32768.0f * cutoff * (float)Math.Sin((float)Math.PI * xx) / ((float)Math.PI * xx) * (float)compute_func(Math.Abs(2.0f * x / N), window_func));
        }

        private static void cubic_coef(short x, short[] interp/*[4]*/)
        {
            /* Compute interpolation coefficients. I'm not sure whether this corresponds to cubic interpolation
            but I know it's MMSE-optimal on a sinc */
            short x2, x3;
            x2 = Inlines.MULT16_16_P15(x, x);
            x3 = Inlines.MULT16_16_P15(x, x2);
            interp[0] = (short)Inlines.PSHR32(Inlines.MULT16_16(Inlines.QCONST16(-0.16667f, 15), x) + Inlines.MULT16_16(Inlines.QCONST16(0.16667f, 15), x3), 15);
            interp[1] = Inlines.EXTRACT16(Inlines.EXTEND32(x) + Inlines.SHR32(Inlines.SUB32(Inlines.EXTEND32(x2), Inlines.EXTEND32(x3)), 1));
            interp[3] = (short)Inlines.PSHR32(Inlines.MULT16_16(Inlines.QCONST16(-0.33333f, 15), x) + Inlines.MULT16_16(Inlines.QCONST16(.5f, 15), x2) - Inlines.MULT16_16(Inlines.QCONST16(0.16667f, 15), x3), 15);
            /* Just to make sure we don't have rounding problems */
            interp[2] = (short)(CeltConstants.Q15_ONE - interp[0] - interp[1] - interp[3]);
            if (interp[2] < 32767)
                interp[2] += 1;
        }

        private int resampler_basic_direct_single(int channel_index, Pointer<short> input, ref int in_len, Pointer<short> output, ref int out_len)
        {
            int N = this.filt_len;
            int out_sample = 0;
            int last_sample = this.last_sample[channel_index];
            int samp_frac_num = this.samp_frac_num[channel_index];
            Pointer<short> sinc_table = this.sinc_table.GetPointer();
            int sum;

            while (!(last_sample >= in_len || out_sample >= out_len))
            {
                Pointer<short> sinct = sinc_table.Point((int)samp_frac_num * N);
                Pointer<short> iptr = input.Point(last_sample);

                int j;
                sum = 0;
                for (j = 0; j < N; j++) sum += Inlines.MULT16_16(sinct[j], iptr[j]);

                /*    This code is slower on most DSPs which have only 2 accumulators.
                      Plus this this forces truncation to 32 bits and you lose the HW guard bits.
                      I think we can trust the compiler and let it vectorize and/or unroll itself.
                      spx_word32_t accum[4] = {0,0,0,0};
                      for(j=0;j<N;j+=4) {
                        accum[0] += MULT16_16(sinct[j], iptr[j]);
                        accum[1] += MULT16_16(sinct[j+1], iptr[j+1]);
                        accum[2] += MULT16_16(sinct[j+2], iptr[j+2]);
                        accum[3] += MULT16_16(sinct[j+3], iptr[j+3]);
                      }
                      sum = accum[0] + accum[1] + accum[2] + accum[3];
                */

                output[this.out_stride * out_sample++] = Inlines.SATURATE16(Inlines.PSHR32(sum, 15));
                last_sample += this.int_advance;
                samp_frac_num += (int)this.frac_advance;
                if (samp_frac_num >= this.den_rate)
                {
                    samp_frac_num -= this.den_rate;
                    last_sample++;
                }
            }

            this.last_sample[channel_index] = last_sample;
            this.samp_frac_num[channel_index] = samp_frac_num;
            return out_sample;
        }

        private int resampler_basic_interpolate_single(int channel_index, Pointer<short> input, ref int in_len, Pointer<short> output, ref int out_len)
        {
            int N = this.filt_len;
            int out_sample = 0;
            int last_sample = this.last_sample[channel_index];
            int samp_frac_num = this.samp_frac_num[channel_index];
            int sum;

            while (!(last_sample >= in_len || out_sample >= out_len))
            {
                Pointer<short> iptr = input.Point(last_sample);

                int offset = samp_frac_num * this.oversample / this.den_rate;
                short frac = (short)Inlines.PDIV32(Inlines.SHL32((samp_frac_num * this.oversample) % this.den_rate, 15), this.den_rate);
                short[] interp = new short[4];


                int j;
                int[] accum = { 0, 0, 0, 0 };

                for (j = 0; j < N; j++)
                {
                    short curr_in = iptr[j];
                    accum[0] += Inlines.MULT16_16(curr_in, this.sinc_table[4 + (j + 1) * (int)this.oversample - offset - 2]);
                    accum[1] += Inlines.MULT16_16(curr_in, this.sinc_table[4 + (j + 1) * (int)this.oversample - offset - 1]);
                    accum[2] += Inlines.MULT16_16(curr_in, this.sinc_table[4 + (j + 1) * (int)this.oversample - offset]);
                    accum[3] += Inlines.MULT16_16(curr_in, this.sinc_table[4 + (j + 1) * (int)this.oversample - offset + 1]);
                }

                cubic_coef(frac, interp);
                sum = Inlines.MULT16_32_Q15(interp[0], Inlines.SHR32(accum[0], 1)) + Inlines.MULT16_32_Q15(interp[1], Inlines.SHR32(accum[1], 1)) + Inlines.MULT16_32_Q15(interp[2], Inlines.SHR32(accum[2], 1)) + Inlines.MULT16_32_Q15(interp[3], Inlines.SHR32(accum[3], 1));

                output[out_stride * out_sample++] = Inlines.SATURATE16(Inlines.PSHR32(sum, 14));
                last_sample += int_advance;
                samp_frac_num += (int)frac_advance;
                if (samp_frac_num >= den_rate)
                {
                    samp_frac_num -= den_rate;
                    last_sample++;
                }
            }

            this.last_sample[channel_index] = last_sample;
            this.samp_frac_num[channel_index] = samp_frac_num;
            return out_sample;
        }

        private void update_filter()
        {
            int old_length;

            old_length = this.filt_len;
            this.oversample = QualityMapping.quality_map[this.quality].oversample;
            this.filt_len = QualityMapping.quality_map[this.quality].base_length;

            if (this.num_rate > this.den_rate)
            {
                /* down-sampling */
                this.cutoff = QualityMapping.quality_map[this.quality].downsample_bandwidth * this.den_rate / this.num_rate;
                /* FIXME: divide the numerator and denominator by a certain amount if they're too large */
                this.filt_len = this.filt_len * this.num_rate / this.den_rate;
                /* Round up to make sure we have a multiple of 8 */
                this.filt_len = ((this.filt_len - 1) & (~0x7)) + 8;
                if (2 * this.den_rate < this.num_rate)
                    this.oversample >>= 1;
                if (4 * this.den_rate < this.num_rate)
                    this.oversample >>= 1;
                if (8 * this.den_rate < this.num_rate)
                    this.oversample >>= 1;
                if (16 * this.den_rate < this.num_rate)
                    this.oversample >>= 1;
                if (this.oversample < 1)
                    this.oversample = 1;
            }
            else {
                /* up-sampling */
                this.cutoff = QualityMapping.quality_map[this.quality].upsample_bandwidth;
            }

            if (this.den_rate <= 16 * (this.oversample + 8))
            {
                int i;
                if (this.sinc_table == null)
                    this.sinc_table = new short[this.filt_len * this.den_rate];
                else if (this.sinc_table_length < this.filt_len * this.den_rate)
                {
                    this.sinc_table = new short[this.filt_len * this.den_rate];
                    this.sinc_table_length = this.filt_len * this.den_rate;
                }
                for (i = 0; i < this.den_rate; i++)
                {
                    int j;
                    for (j = 0; j < this.filt_len; j++)
                    {
                        this.sinc_table[i * this.filt_len + j] = sinc(this.cutoff, ((j - (int)this.filt_len / 2 + 1) - ((float)i) / this.den_rate), this.filt_len, QualityMapping.quality_map[this.quality].window_func);
                    }
                }
                this.resampler_ptr = resampler_basic_direct_single;
                /*fprintf (stderr, "resampler uses direct sinc table and normalised cutoff %f\n", cutoff);*/
            }
            else {
                int i;
                if (this.sinc_table == null)
                    this.sinc_table = new short[this.filt_len * this.oversample + 8];
                else if (this.sinc_table_length < this.filt_len * this.oversample + 8)
                {
                    this.sinc_table = new short[this.filt_len * this.oversample + 8];
                    this.sinc_table_length = this.filt_len * this.oversample + 8;
                }
                for (i = -4; i < (int)(this.oversample * this.filt_len + 4); i++)
                    this.sinc_table[i + 4] = sinc(this.cutoff, (i / (float)this.oversample - this.filt_len / 2), this.filt_len, QualityMapping.quality_map[this.quality].window_func);
                this.resampler_ptr = resampler_basic_interpolate_single;
                /*fprintf (stderr, "resampler uses interpolated sinc table and normalised cutoff %f\n", cutoff);*/
            }
            this.int_advance = this.num_rate / this.den_rate;
            this.frac_advance = this.num_rate % this.den_rate;


            /* Here's the place where we update the filter memory to take into account
               the change in filter length. It's probably the messiest part of the code
               due to handling of lots of corner cases. */
            if (this.mem == null)
            {
                int i;
                this.mem_alloc_size = this.filt_len - 1 + this.buffer_size;
                this.mem = new short[this.nb_channels * this.mem_alloc_size];
                for (i = 0; i < this.nb_channels * this.mem_alloc_size; i++)
                    this.mem[i] = 0;
                /*speex_warning("init filter");*/
            }
            else if (this.started == 0)
            {
                int i;
                this.mem_alloc_size = this.filt_len - 1 + this.buffer_size;
                this.mem = new short[this.nb_channels * this.mem_alloc_size];
                for (i = 0; i < this.nb_channels * this.mem_alloc_size; i++)
                    this.mem[i] = 0;
                /*speex_warning("reinit filter");*/
            }
            else if (this.filt_len > old_length)
            {
                int i;
                /* Increase the filter length */
                /*speex_warning("increase filter size");*/
                int old_alloc_size = this.mem_alloc_size;
                if ((this.filt_len - 1 + this.buffer_size) > this.mem_alloc_size)
                {
                    this.mem_alloc_size = this.filt_len - 1 + this.buffer_size;
                    this.mem = new short[this.nb_channels * this.mem_alloc_size];
                }
                for (i = this.nb_channels - 1; i >= 0; i--)
                {
                    int j;
                    int olen = old_length;
                    /*if (st.magic_samples[i])*/
                    {
                        /* Try and remove the magic samples as if nothing had happened */

                        /* FIXME: This is wrong but for now we need it to avoid going over the array bounds */
                        olen = old_length + 2 * this.magic_samples[i];
                        for (j = old_length - 2 + this.magic_samples[i]; j >= 0; j--)
                            this.mem[i * this.mem_alloc_size + j + this.magic_samples[i]] = this.mem[i * old_alloc_size + j];
                        for (j = 0; j < this.magic_samples[i]; j++)
                            this.mem[i * this.mem_alloc_size + j] = 0;
                        this.magic_samples[i] = 0;
                    }
                    if (this.filt_len > olen)
                    {
                        /* If the new filter length is still bigger than the "augmented" length */
                        /* Copy data going backward */
                        for (j = 0; j < olen - 1; j++)
                            this.mem[i * this.mem_alloc_size + (this.filt_len - 2 - j)] = this.mem[i * this.mem_alloc_size + (olen - 2 - j)];
                        /* Then put zeros for lack of anything better */
                        for (; j < this.filt_len - 1; j++)
                            this.mem[i * this.mem_alloc_size + (this.filt_len - 2 - j)] = 0;
                        /* Adjust last_sample */
                        this.last_sample[i] += (this.filt_len - olen) / 2;
                    }
                    else {
                        /* Put back some of the magic! */
                        this.magic_samples[i] = (olen - this.filt_len) / 2;
                        for (j = 0; j < this.filt_len - 1 + this.magic_samples[i]; j++)
                            this.mem[i * this.mem_alloc_size + j] = this.mem[i * this.mem_alloc_size + j + this.magic_samples[i]];
                    }
                }
            }
            else if (this.filt_len < old_length)
            {
                int i;
                /* Reduce filter length, this a bit tricky. We need to store some of the memory as "magic"
                   samples so they can be used directly as input the next time(s) */
                for (i = 0; i < this.nb_channels; i++)
                {
                    int j;
                    int old_magic = this.magic_samples[i];
                    this.magic_samples[i] = (old_length - this.filt_len) / 2;
                    /* We must copy some of the memory that's no longer used */
                    /* Copy data going backward */
                    for (j = 0; j < this.filt_len - 1 + this.magic_samples[i] + old_magic; j++)
                        this.mem[i * this.mem_alloc_size + j] = this.mem[i * this.mem_alloc_size + j + this.magic_samples[i]];
                    this.magic_samples[i] += old_magic;
                }
            }
        }

        private void speex_resampler_process_native(int channel_index, ref int in_len, Pointer<short> output, ref int out_len)
        {
            int j = 0;
            int N = this.filt_len;
            int out_sample = 0;
            Pointer<short> mem = this.mem.GetPointer(channel_index * this.mem_alloc_size);
            int ilen;

            this.started = 1;

            /* Call the right resampler through the function ptr */
            out_sample = this.resampler_ptr(channel_index, mem, ref in_len, output, ref out_len);

            if (this.last_sample[channel_index] < (int)in_len)
                in_len = this.last_sample[channel_index];
            out_len = out_sample;
            this.last_sample[channel_index] -= in_len;

            ilen = in_len;

            for (j = 0; j < N - 1; ++j)
                mem[j] = mem[j + ilen];
        }

        private int speex_resampler_magic(int channel_index, BoxedValue<Pointer<short>> output, int out_len)
        {
            int tmp_in_len = this.magic_samples[channel_index];
            Pointer<short> mem = this.mem.GetPointer(channel_index * this.mem_alloc_size);
            int N = this.filt_len;

            this.speex_resampler_process_native(channel_index, ref tmp_in_len, output.Val, ref out_len);

            this.magic_samples[channel_index] -= tmp_in_len;

            /* If we couldn't process all "magic" input samples, save the rest for next time */
            if (this.magic_samples[channel_index] != 0)
            {
                int i;
                for (i = 0; i < this.magic_samples[channel_index]; i++)
                    mem[N - 1 + i] = mem[N - 1 + i + tmp_in_len];
            }
            output.Val = output.Val.Point(out_len * this.out_stride);
            return out_len;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Create a new resampler with integer input and output rates (in hertz).
        /// </summary>
        /// <param name="nb_channels">The number of channels to be processed</param>
        /// <param name="in_rate">Input sampling rate, in hertz</param>
        /// <param name="out_rate">Output sampling rate, in hertz</param>
        /// <param name="quality">Resampling quality, from 0 to 10</param>
        /// <returns>A newly created restampler</returns>
        public static SpeexResampler Create(int nb_channels, int in_rate, int out_rate, int quality)
        {
            return Create(nb_channels, in_rate, out_rate, in_rate, out_rate, quality);
        }

        /// <summary>
        /// Create a new resampler with fractional input/output rates. The sampling 
        /// rate ratio is an arbitrary rational number with both the numerator and
        /// denominator being 32-bit integers.
        /// </summary>
        /// <param name="nb_channels">The number of channels to be processed</param>
        /// <param name="ratio_num">Numerator of sampling rate ratio</param>
        /// <param name="ratio_den">Denominator of sampling rate ratio</param>
        /// <param name="in_rate">Input sample rate rounded to the nearest integer (in hz)</param>
        /// <param name="out_rate">Output sample rate rounded to the nearest integer (in hz)</param>
        /// <param name="quality">Resampling quality, from 0 to 10</param>
        /// <returns>A newly created restampler</returns>
        public static SpeexResampler Create(int nb_channels, int ratio_num, int ratio_den, int in_rate, int out_rate, int quality)
        {
            int i;
            SpeexResampler st;
            if (quality > 10 || quality < 0)
            {
                throw new ArgumentException("Quality must be between 0 and 10");
            }
            st = new SpeexResampler();
            st.initialised = 0;
            st.started = 0;
            st.in_rate = 0;
            st.out_rate = 0;
            st.num_rate = 0;
            st.den_rate = 0;
            st.quality = -1;
            st.sinc_table_length = 0;
            st.mem_alloc_size = 0;
            st.filt_len = 0;
            st.mem = null;
            st.resampler_ptr = null;
            st.cutoff = 1.0f;
            st.nb_channels = nb_channels;
            st.in_stride = 1;
            st.out_stride = 1;
            st.buffer_size = 160;

            /* Per channel data */
            st.last_sample = new int[nb_channels];
            st.magic_samples = new int[nb_channels];
            st.samp_frac_num = new int[nb_channels];
            for (i = 0; i < nb_channels; i++)
            {
                st.last_sample[i] = 0;
                st.magic_samples[i] = 0;
                st.samp_frac_num[i] = 0;
            }

            st.Quality = quality;
            st.SetRateFraction(ratio_num, ratio_den, in_rate, out_rate);
            
            st.update_filter();

            st.initialised = 1;

            return st;
        }

        /// <summary>
        /// Resample an int array. The input and output buffers must *not* overlap
        /// </summary>
        /// <param name="channel_index">The index of the channel to process (for multichannel input, 0 otherwise)</param>
        /// <param name="input">Input buffer</param>
        /// <param name="in_len">Number of input samples in the input buffer. After this function returns, this value
        /// will be set to the number of input samples actually processed</param>
        /// <param name="output">Output buffer</param>
        /// <param name="out_len">Size of the output buffer. After this function returns, this value will be set to the number
        /// of output samples actually written</param>
        public void Process(int channel_index, Pointer<short> input, ref int in_len, Pointer<short> output, ref int out_len)
        {
            int j;
            int ilen = in_len;
            int olen = out_len;
            Pointer<short> x = this.mem.GetPointer(channel_index * this.mem_alloc_size);
            int filt_offs = this.filt_len - 1;
            int xlen = this.mem_alloc_size - filt_offs;
            int istride = this.in_stride;

            if (this.magic_samples[channel_index] != 0)
            {
                BoxedValue<Pointer<short>> boxed_output = new BoxedValue<Pointer<short>>(output);
                olen -= this.speex_resampler_magic(channel_index, boxed_output, olen);
                output = boxed_output.Val;
            }
            if (this.magic_samples[channel_index] == 0)
            {
                while (ilen != 0 && olen != 0)
                {
                    int ichunk = (ilen > xlen) ? xlen : ilen;
                    int ochunk = olen;

                    if (input != null)
                    {
                        for (j = 0; j < ichunk; ++j)
                            x[j + filt_offs] = input[j * istride];
                    }
                    else {
                        for (j = 0; j < ichunk; ++j)
                            x[j + filt_offs] = 0;
                    }
                    this.speex_resampler_process_native(channel_index, ref ichunk, output, ref ochunk);
                    ilen -= ichunk;
                    olen -= ochunk;
                    output = output.Point(ochunk * this.out_stride);
                    if (input != null)
                        input = input.Point(ichunk * istride);
                }
            }
            in_len -= ilen;
            out_len -= olen;
        }

        /// <summary>
        /// Resample a float array array. The input and output buffers must *not* overlap
        /// </summary>
        /// <param name="channel_index">The index of the channel to process (for multichannel input, 0 otherwise)</param>
        /// <param name="input">Input buffer</param>
        /// <param name="in_len">Number of input samples in the input buffer. After this function returns, this value
        /// will be set to the number of input samples actually processed</param>
        /// <param name="output">Output buffer</param>
        /// <param name="out_len">Size of the output buffer. After this function returns, this value will be set to the number
        /// of output samples actually written</param>
        public void Process(int channel_index, Pointer<float> input, ref int in_len, Pointer<float> output, ref int out_len)
        {
            int j;
            int istride_save = this.in_stride;
            int ostride_save = this.out_stride;
            int ilen = in_len;
            int olen = out_len;
            Pointer<short> x = this.mem.GetPointer(channel_index * this.mem_alloc_size);
            int xlen = this.mem_alloc_size - (this.filt_len - 1);
            int ylen = (olen < FIXED_STACK_ALLOC) ? olen : FIXED_STACK_ALLOC;
            Pointer<short> ystack = new Pointer<short>(new short[ylen]);

            this.out_stride = 1;

            while (ilen != 0 && olen != 0)
            {
                Pointer<short> y = ystack;
                int ichunk = (ilen > xlen) ? xlen : ilen;
                int ochunk = (olen > ylen) ? ylen : olen;
                int omagic = 0;

                if (this.magic_samples[channel_index] != 0)
                {
                    BoxedValue<Pointer<short>> boxed_y = new BoxedValue<Pointer<short>>(y);
                    omagic = this.speex_resampler_magic(channel_index, boxed_y, ochunk);
                    y = boxed_y.Val;
                    ochunk -= omagic;
                    olen -= omagic;
                }
                if (this.magic_samples[channel_index] == 0)
                {
                    if (input != null)
                    {
                        for (j = 0; j < ichunk; ++j)
                            x[j + this.filt_len - 1] = WORD2INT(input[j * istride_save]);
                    }
                    else {
                        for (j = 0; j < ichunk; ++j)
                            x[j + this.filt_len - 1] = 0;
                    }

                    this.speex_resampler_process_native(channel_index, ref ichunk, y, ref ochunk);
                }
                else {
                    ichunk = 0;
                    ochunk = 0;
                }

                for (j = 0; j < ochunk + omagic; ++j)
                    output[j * ostride_save] = ystack[j];

                ilen -= ichunk;
                olen -= ochunk;
                output = output.Point((ochunk + omagic) * ostride_save);
                if (input != null)
                    input = input.Point(ichunk * istride_save);
            }
            this.out_stride = ostride_save;
            in_len -= ilen;
            out_len -= olen;
        }

        /// <summary>
        /// Resamples an interleaved int array. The stride is determined by the number of channels of the resampler.
        /// </summary>
        /// <param name="input">Input buffer</param>
        /// <param name="in_len">The number of samples *PER-CHANNEL* in the input buffer. After this function returns, this
        /// value will be set to the number of input samples actually processed</param>
        /// <param name="output">Output buffer</param>
        /// <param name="out_len">The size of the output buffer in samples-per-channel. After this function returns, this value
        /// will be set to the number of samples per channel actually output</param>
        public void ProcessInterleaved(Pointer<float> input, ref int in_len, Pointer<float> output, ref int out_len)
        {
            int i;
            int istride_save, ostride_save;
            int bak_out_len = out_len;
            int bak_in_len = in_len;
            istride_save = this.in_stride;
            ostride_save = this.out_stride;
            this.in_stride = this.out_stride = this.nb_channels;
            for (i = 0; i < this.nb_channels; i++)
            {
                out_len = bak_out_len;
                in_len = bak_in_len;
                if (input != null)
                    this.Process(i, input.Point(i), ref in_len, output.Point(i), ref out_len);
                else
                    this.Process(i, null, ref in_len, output.Point(i), ref out_len);
            }
            this.in_stride = istride_save;
            this.out_stride = ostride_save;
        }

        /// <summary>
        /// Resamples an interleaved float array. The stride is determined by the number of channels of the resampler.
        /// </summary>
        /// <param name="input">Input buffer</param>
        /// <param name="in_len">The number of samples *PER-CHANNEL* in the input buffer. After this function returns, this
        /// value will be set to the number of input samples actually processed</param>
        /// <param name="output">Output buffer</param>
        /// <param name="out_len">The size of the output buffer in samples-per-channel. After this function returns, this value
        /// will be set to the number of samples per channel actually output</param>
        public void ProcessInterleaved(Pointer<short> input, ref int in_len, Pointer<short> output, ref int out_len)
        {
            int i;
            int istride_save, ostride_save;
            int bak_out_len = out_len;
            int bak_in_len = in_len;
            istride_save = this.in_stride;
            ostride_save = this.out_stride;
            this.in_stride = this.out_stride = this.nb_channels;
            for (i = 0; i < this.nb_channels; i++)
            {
                out_len = bak_out_len;
                in_len = bak_in_len;
                if (input != null)
                    this.Process(i, input.Point(i), ref in_len, output.Point(i), ref out_len);
                else
                    this.Process(i, null, ref in_len, output.Point(i), ref out_len);
            }
            this.in_stride = istride_save;
            this.out_stride = ostride_save;
        }

        /// <summary>
        /// Make sure that the first samples to go out of the resamplers don't have 
        /// leading zeros. This is only useful before starting to use a newly created
        /// resampler. It is recommended to use that when resampling an audio file, as
        /// it will generate a file with the same length.For real-time processing,
        /// it is probably easier not to use this call (so that the output duration
        /// is the same for the first frame).
        /// </summary>
        public void SkipZeroes()
        {
            int i;
            for (i = 0; i < this.nb_channels; i++)
                this.last_sample[i] = this.filt_len / 2;
        }

        /// <summary>
        /// Resets the resampler so a new (unrelated) stream can be processed.
        /// </summary>
        public void ResetMem()
        {
            int i;
            for (i = 0; i < this.nb_channels; i++)
            {
                this.last_sample[i] = 0;
                this.magic_samples[i] = 0;
                this.samp_frac_num[i] = 0;
            }
            for (i = 0; i < this.nb_channels * (this.filt_len - 1); i++)
                this.mem[i] = 0;
        }

        #endregion

        #region Getters and Setters

        /// <summary>
        /// Sets the input and output rates
        /// </summary>
        /// <param name="in_rate">Input sampling rate, in hertz</param>
        /// <param name="out_rate">Output sampling rate, in hertz</param>
        public void SetRates(int in_rate, int out_rate)
        {
            this.SetRateFraction(in_rate, out_rate, in_rate, out_rate);
        }

        /// <summary>
        /// Get the current input/output sampling rates (integer value).
        /// </summary>
        /// <param name="in_rate">(Output) Sampling rate of input</param>
        /// <param name="out_rate">(Output) Sampling rate of output</param>
        public void GetRates(out int in_rate, out int out_rate)
        {
            in_rate = this.in_rate;
            out_rate = this.out_rate;
        }

        /// <summary>
        /// Sets the input/output sampling rates and resampling ration (fractional values in Hz supported)
        /// </summary>
        /// <param name="ratio_num">Numerator of the sampling rate ratio</param>
        /// <param name="ratio_den">Denominator of the sampling rate ratio</param>
        /// <param name="in_rate">Input sampling rate rounded to the nearest integer (in Hz)</param>
        /// <param name="out_rate">Output sampling rate rounded to the nearest integer (in Hz)</param>
        public void SetRateFraction(int ratio_num, int ratio_den, int in_rate, int out_rate)
        {
            int fact;
            int old_den;
            int i;
            if (this.in_rate == in_rate && this.out_rate == out_rate && this.num_rate == ratio_num && this.den_rate == ratio_den)
                return;

            old_den = this.den_rate;
            this.in_rate = in_rate;
            this.out_rate = out_rate;
            this.num_rate = ratio_num;
            this.den_rate = ratio_den;
            /* FIXME: This is terribly inefficient, but who cares (at least for now)? */
            for (fact = 2; fact <= Inlines.IMIN(this.num_rate, this.den_rate); fact++)
            {
                while ((this.num_rate % fact == 0) && (this.den_rate % fact == 0))
                {
                    this.num_rate /= fact;
                    this.den_rate /= fact;
                }
            }

            if (old_den > 0)
            {
                for (i = 0; i < this.nb_channels; i++)
                {
                    this.samp_frac_num[i] = this.samp_frac_num[i] * this.den_rate / old_den;
                    /* Safety net */
                    if (this.samp_frac_num[i] >= this.den_rate)
                        this.samp_frac_num[i] = this.den_rate - 1;
                }
            }

            if (this.initialised != 0)
                this.update_filter();
        }

        /// <summary>
        /// Gets the current resampling ratio. This will be reduced to the least common denominator
        /// </summary>
        /// <param name="ratio_num">(Output) numerator of the sampling rate ratio</param>
        /// <param name="ratio_den">(Output) denominator of the sampling rate ratio</param>
        public void GetRateFraction(out int ratio_num, out int ratio_den)
        {
            ratio_num = this.num_rate;
            ratio_den = this.den_rate;
        }

        /// <summary>
        /// Gets or sets the resampling quality between 0 and 10, where 0 has poor 
        /// quality and 10 has very high quality.
        /// </summary>
        public int Quality
        {
            get
            {
                return this.quality;
            }
            set
            {
                if (value > 10 || value < 0)
                    throw new ArgumentException("Quality must be between 0 and 10");
                if (this.quality == value)
                    return;
                this.quality = value;
                if (this.initialised != 0)
                    this.update_filter();
            }
        }

        /// <summary>
        /// Gets or sets the input stride
        /// </summary>
        public int InputStride
        {
            get
            {
                return this.in_stride;
            }
            set
            {
                this.in_stride = value;
            }
        }

        /// <summary>
        /// Gets or sets the output stride
        /// </summary>
        public int OutputStride
        {
            get
            {
                return this.out_stride;
            }
            set
            {
                this.out_stride = value;
            }
        }

        /// <summary>
        /// Get the latency introduced by the resampler measured in input samples.
        /// </summary>
        public int InputLatency
        {
            get
            {
                return this.filt_len / 2;
            }
        }

        /// <summary>
        /// Gets the latency introduced by the resampler measured in output samples.
        /// </summary>
        public int OutputLatency
        {
            get
            {
                return ((this.filt_len / 2) * this.den_rate + (this.num_rate >> 1)) / this.num_rate;
            }
        }

        #endregion
    }
}