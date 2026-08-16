using HalconDotNet;
using Serilog;
using VisionMotionAlignment.Models.Vision;
using VisionMotionAlignment.Services.Interfaces;

namespace VisionMotionAlignment.Services.Vision;

/// <summary>
/// 泡罩药丸检测服务：基于 Halcon GMM 分类器识别药丸类型与缺陷。
/// 训练（Train）用参考图学黄/红/绿三类颜色分布并生成 15 格；检测（Check）配准对齐后逐像素分类、逐类认领格子、灰度偏差判缺药/错药。
/// </summary>
public sealed class BlisterCheckService : IBlisterCheckService
{
    // ════════════════════════════════════════════════════════════════
    // 检测参数常量（集中管理，可读性 C4，取值来自附录 A 参数表）
    // ════════════════════════════════════════════════════════════════

    /// <summary>泡罩板阈值下限。</summary>
    private const int ThresholdMin = 90;

    /// <summary>泡罩板阈值上限。</summary>
    private const int ThresholdMax = 255;

    /// <summary>泡罩板最小面积（训练阶段 select_shape）。</summary>
    private const int BlisterMinArea = 5000;

    /// <summary>泡罩板最大面积（训练阶段 select_shape）。</summary>
    private const int BlisterMaxArea = 999999;

    /// <summary>泡罩板最大面积（检测阶段 select_shape）。</summary>
    private const int BlisterMaxAreaCheck = 9999999;

    /// <summary>chamber 行数。</summary>
    private const int ChamberRows = 5;

    /// <summary>chamber 列数。</summary>
    private const int ChamberCols = 3;

    /// <summary>各 chamber 行的中心坐标（px，参考图实测药丸中心）。</summary>
    private static readonly double[] ChamberRowCenters = [106, 169, 239, 306, 383];

    /// <summary>各 chamber 列的中心坐标（px，参考图实测药丸中心）。</summary>
    private static readonly double[] ChamberColCenters = [169, 315, 485];

    /// <summary>chamber 半宽（px）。</summary>
    private const int ChamberHalfWidth = 64;

    /// <summary>chamber 半高（px）。</summary>
    private const int ChamberHalfHeight = 30;

    /// <summary>黄药丸 B 通道阈值下限（黄色在 B 通道较暗）。</summary>
    private const int YellowBMin = 60;

    /// <summary>黄药丸 B 通道阈值上限。</summary>
    private const int YellowBMax = 95;

    /// <summary>红药丸迟滞低阈值（红色在 B 通道反向后偏亮）。</summary>
    private const int RedLowThreshold = 190;

    /// <summary>红药丸迟滞高阈值。</summary>
    private const int RedHighThreshold = 200;

    /// <summary>红药丸最小连通长度。</summary>
    private const int RedMinLength = 5;

    /// <summary>绿药丸迟滞低阈值。</summary>
    private const int GreenLowThreshold = 180;

    /// <summary>绿药丸迟滞高阈值。</summary>
    private const int GreenHighThreshold = 200;

    /// <summary>绿药丸最小连通长度。</summary>
    private const int GreenMinLength = 10;

    /// <summary>第一类（黄）行范围下限（对应行中心 106）。</summary>
    private const int PillType1RowMin = 1;

    /// <summary>第一类（黄）行范围上限（分隔黄行与红行）。</summary>
    private const int PillType1RowMax = 138;

    /// <summary>第二类（红）行范围下限。</summary>
    private const int PillType2RowMin = 138;

    /// <summary>第二类（红）行范围上限（分隔红行与绿行）。</summary>
    private const int PillType2RowMax = 273;

    /// <summary>第三类（绿）行范围下限。</summary>
    private const int PillType3RowMin = 273;

    /// <summary>第三类（绿）行范围上限。</summary>
    private const int PillType3RowMax = 390;

    /// <summary>各类期望药丸数量（黄/红/绿）。</summary>
    private static readonly int[] PillTypeCount = [3, 6, 6];

    /// <summary>GMM 特征数（RGB 3 通道）。</summary>
    private const int GmmNumFeatures = 3;

    /// <summary>GMM 类别数（黄/红/绿）。</summary>
    private const int GmmNumClasses = 3;

    /// <summary>每类中心数（小类别 1 个，大类 5 个）。</summary>
    private static readonly HTuple GmmCenters = new HTuple(1).TupleConcat(5);

    /// <summary>GMM 协方差类型（球面协方差，计算量小且稳健）。</summary>
    private const string GmmCovarianceType = "spherical";

    /// <summary>GMM 预处理（归一化，消除光照差异）。</summary>
    private const string GmmPreprocessing = "normalization";

    /// <summary>GMM 随机种子（保证训练可复现）。</summary>
    private const int GmmRandSeed = 42;

    /// <summary>训练最大迭代次数。</summary>
    private const int GmmMaxIter = 100;

    /// <summary>训练聚类阈值。</summary>
    private const double GmmClusterThreshold = 0.001;

    /// <summary>训练终止阈值。</summary>
    private const double GmmEndThreshold = 0.0001;

    /// <summary>分类概率阈值（低于此概率的像素不归入任何类）。</summary>
    private const double ClassifyThreshold = 0.0005;

    /// <summary>药丸最小面积（select_shape 筛选）。</summary>
    private const double PillMinArea = 200;

    /// <summary>药丸最大面积。</summary>
    private const double PillMaxArea = 3000;

    /// <summary>药丸最小宽度。</summary>
    private const double PillMinWidth = 40;

    /// <summary>药丸最大宽度。</summary>
    private const double PillMaxWidth = 80;

    /// <summary>chamber 残留区域最大面积（排除边框残留）。</summary>
    private const double ChamberRemnantMaxArea = 7868;

    /// <summary>错药判定灰度偏差阈值（偏差大于此值视为错药，否则视为缺药）。</summary>
    private const double WrongPillDeviationThreshold = 40;

    // ════════════════════════════════════════════════════════════════
    // 训练后的共享状态（Train 写入，Check 读取）
    // ════════════════════════════════════════════════════════════════

    /// <summary>GMM 分类器句柄。</summary>
    private HTuple _gmmHandle = new();

    /// <summary>15 个 chamber 矩形区域。</summary>
    private HObject _chambers = new();

    /// <summary>chamber 并集区域。</summary>
    private HObject _chambersUnion = new();

    /// <summary>参考图泡罩板角度（弧度，已加 180° 归一化）。</summary>
    private HTuple _phiRef = new();

    /// <summary>参考图泡罩板中心 Row。</summary>
    private HTuple _rowRef = new();

    /// <summary>参考图泡罩板中心 Column。</summary>
    private HTuple _columnRef = new();

    /// <summary>是否已训练完成。</summary>
    private bool _isTrained;

    /// <summary>Dispose 标志，防止重复释放。</summary>
    private bool _disposed;

    /// <summary>Train/Check 串行化锁（任务书 8.1：Train 和 Check 不可并发）。</summary>
    private readonly object _lock = new();

    /// <inheritdoc/>
    public bool IsTrained
    {
        get { lock (_lock) return _isTrained; }
    }

    /// <inheritdoc/>
    public int[] GetExpectedCounts()
    {
        lock (_lock)
        {
            return _isTrained ? (int[])PillTypeCount.Clone() : [];
        }
    }

    /// <summary>
    /// 用参考图训练 GMM 分类器（一次性操作）。
    ///
    /// 【流程】
    /// 1. 从参考图提取泡罩板轮廓、15 个 chamber 网格和三类药丸像素（<see cref="ExtractPillTypes"/>）
    /// 2. 创建 GMM 分类器并训练（create_class_gmm → add_samples_image_class_gmm → train_class_gmm）
    /// 3. 保存训练产物（GMM 句柄、chamber 网格、参考几何）到字段，供 <see cref="Check"/> 反复使用
    ///
    /// 【线程安全】
    /// 方法内部持有 <see cref="_lock"/>，与 <see cref="Check"/> 互斥，不可并发调用。
    ///
    /// 【调用约定】
    /// - 训练只需调用一次；训练完成后可反复调用 <see cref="Check"/> 检测多张图，无需重复训练
    /// - 参考图必须是彩色（RGB）HImage，泡罩板在亮背景下
    /// - 训练失败时抛出 <see cref="InvalidOperationException"/>，调用方须捕获并提示用户
    /// </summary>
    /// <param name="referenceImage">参考图（彩色 HImage，含标准药丸组合）。</param>
    /// <exception cref="ArgumentNullException">参考图为 null 时抛出。</exception>
    /// <exception cref="InvalidOperationException">训练失败时抛出（含 Halcon 算子异常）。</exception>
    public void Train(HImage referenceImage)
    {
        if (referenceImage is null)
        {
            throw new ArgumentNullException(nameof(referenceImage));
        }

        lock (_lock)
        {
            try
            {
                // ════════════════════════════════════════════════════════════
                // 第 1 步：从参考图提取泡罩板轮廓、chamber 网格和三类药丸像素
                // ════════════════════════════════════════════════════════════
                ExtractPillTypes(referenceImage, out HObject chambers, out HObject chambersUnion,
                    out HObject classes, out HTuple phiRef, out HTuple rowRef, out HTuple columnRef);

                // ════════════════════════════════════════════════════════════
                // 第 2 步：创建 GMM 分类器并训练
                // ════════════════════════════════════════════════════════════
                // create_class_gmm(特征数=3, 类别数=3, 每类中心数=[1,5], 协方差=spherical,
                //                 预处理=normalization, 组件数=10, 种子=42)
                // 注意：gmmHandle 训练后要保存到 _gmmHandle 供 Check 使用，不能用 using 释放。
                //       其所有权在 Dispose() 里由 ClearClassGmm 统一释放。
                HOperatorSet.CreateClassGmm(GmmNumFeatures, GmmNumClasses, GmmCenters,
                    GmmCovarianceType, GmmPreprocessing, 10, GmmRandSeed, out HTuple gmmHandle);
                // 用参考图 + 提取的药丸类别区域作为训练样本
                HOperatorSet.AddSamplesImageClassGmm(referenceImage, classes, gmmHandle, 0);
                // train_class_gmm(模型, 最大迭代=100, 聚类阈值=0.001, "training", 终止阈值=0.0001)
                HOperatorSet.TrainClassGmm(gmmHandle, GmmMaxIter, GmmClusterThreshold,
                    "training", GmmEndThreshold, out _, out _);

                // ════════════════════════════════════════════════════════════
                // 第 3 步：保存训练产物到字段（Check 阶段使用）
                // ════════════════════════════════════════════════════════════
                _gmmHandle.Dispose();
                _gmmHandle = gmmHandle;
                _chambers.Dispose();
                _chambers = chambers;
                _chambersUnion.Dispose();
                _chambersUnion = chambersUnion;
                _phiRef.Dispose();
                _phiRef = phiRef;
                _rowRef.Dispose();
                _rowRef = rowRef;
                _columnRef.Dispose();
                _columnRef = columnRef;
                _isTrained = true;

                Log.Logger.Information("泡罩药丸检测：GMM 分类器训练完成（3 类，期望数量 [{Counts}]）",
                    string.Join(",", PillTypeCount));
            }
            catch (Exception ex)
            {
                // 训练失败：重置状态，避免半训练状态被 Check 使用
                _isTrained = false;
                Log.Logger.Error(ex, "泡罩药丸检测：GMM 训练失败");
                throw new InvalidOperationException("泡罩药丸 GMM 分类器训练失败", ex);
            }
        }
    }

    /// <inheritdoc/>
    public BlisterCheckResult Check(HImage testImage)
    {
        if (testImage is null)
        {
            Log.Logger.Warning("泡罩药丸检测：输入图像为空");
            return BlisterCheckResult.Invalid;
        }

        lock (_lock)
        {
            if (!_isTrained)
            {
                Log.Logger.Warning("泡罩药丸检测：尚未训练，无法检测");
                return BlisterCheckResult.Invalid;
            }

            try
            {
                // ════════════════════════════════════════════════════════════
                // 第 1 步：图像配准（把测试图对齐到参考图的坐标空间）
                // ════════════════════════════════════════════════════════════
                // 1a. 提取测试图泡罩板轮廓（阈值分割 → 连通域 → 按面积筛选 → 凸包）
                HOperatorSet.Threshold(testImage, out HObject region, ThresholdMin, ThresholdMax);
                using var _r1 = region;
                HOperatorSet.Connection(region, out HObject connectedRegions);
                using var _r2 = connectedRegions;
                HOperatorSet.SelectShape(connectedRegions, out HObject selectedRegions, "area", "and", BlisterMinArea, BlisterMaxAreaCheck);
                using var _r3 = selectedRegions;
                HOperatorSet.ShapeTrans(selectedRegions, out HObject blisterConvex, "convex");
                using var _r4 = blisterConvex;

                // 1b. 计算泡罩板朝向角 Phi；若 |Phi|>90° 加 180° 归一化，
                //     避免 OrientationRegion 的方向歧义（示例代码的固定处理）
                HOperatorSet.OrientationRegion(blisterConvex, out HTuple phi);
                using var _t1 = phi;
                if (Math.Abs(phi.D) > Math.PI / 2)
                {
                    phi = Math.PI + phi.D;
                }

                // 1c. 求泡罩板中心 (Row, Column)，与参考图中心/角度构成刚体变换
                HOperatorSet.AreaCenter(blisterConvex, out _, out HTuple row, out HTuple column);
                using var _t2 = row;
                using var _t3 = column;
                HOperatorSet.VectorAngleToRigid(row, column, phi, _rowRef, _columnRef, _phiRef, out HTuple homMat2D);
                using var _t4 = homMat2D;

                // 1d. 整张测试图按刚体变换旋转平移 → 对齐图（返回给 UI 显示，不释放）
                HOperatorSet.AffineTransImage(testImage, out HObject imageAffineTrans, homMat2D, "constant", "false");

                // 1e. 分解 RGB 通道，B 通道用于后续"错药/缺药"的灰度偏差判断
                HOperatorSet.Decompose3(imageAffineTrans, out HObject imageR, out HObject imageG, out HObject imageB);
                imageR.Dispose();
                imageG.Dispose();

                // 1f. 用 chamber 并集裁剪对齐图，只保留格子内的区域供 GMM 分类
                HOperatorSet.ReduceDomain(imageAffineTrans, _chambersUnion, out HObject imageReduced);
                using var _r5 = imageReduced;

                // ════════════════════════════════════════════════════════════
                // 第 2 步：GMM 逐像素分类
                // ════════════════════════════════════════════════════════════
                // 分类结果 ClassRegions 是一组区域，每个区域代表归入某类的像素连通块
                HOperatorSet.ClassifyImageClassGmm(imageReduced, out HObject classRegions, _gmmHandle, ClassifyThreshold);
                using var _r6 = classRegions;
                HOperatorSet.CountObj(classRegions, out HTuple numClasses);
                using var _t5 = numClasses;

                // ════════════════════════════════════════════════════════════
                // 第 3 步：逐类筛选，得到"真正药丸"区域集 FinalClasses
                // ════════════════════════════════════════════════════════════
                // 原理：每类区域可能包含 chamber 边框/背景噪声，需按面积、宽度筛选出药丸，
                //       再从 chamber 集合中减去已确认药丸，避免重复归类。
                HOperatorSet.GenEmptyObj(out HObject finalClasses);
                HOperatorSet.Connection(_chambers, out HObject chambersRemaining);
                using var _r7 = chambersRemaining;

                for (int index = numClasses.I; index >= 1; index--)
                {
                    HOperatorSet.SelectObj(classRegions, out HObject classRegion, index);
                    using var _r8 = classRegion;

                    // 与"剩余 chamber"求交，只保留格子内的分类像素
                    HOperatorSet.Intersection(chambersRemaining, classRegion, out HObject inChamber);
                    using var _r9 = inChamber;

                    // 按面积[200,3000] + 宽度[40,80]筛选真正的药丸
                    HOperatorSet.SelectShape(inChamber, out HObject pillsOfOneType,
                        new HTuple("area", "width"), "and",
                        new HTuple(PillMinArea, PillMinWidth),
                        new HTuple(PillMaxArea, PillMaxWidth));
                    using var _r10 = pillsOfOneType;

                    // 求 chamber 并集中不属于该类药丸的"残留区域"（边框/噪声）
                    HOperatorSet.Difference(_chambersUnion, pillsOfOneType, out HObject regionDifference);
                    using var _r11 = regionDifference;
                    HOperatorSet.Connection(regionDifference, out HObject connectedRemnants);
                    using var _r12 = connectedRemnants;

                    // 残留区域面积 ≤7868 才保留（过大的是误删的整体区域，丢弃）
                    HOperatorSet.SelectShape(connectedRemnants, out HObject selectedRemnants, "area", "and", 0, ChamberRemnantMaxArea);
                    using var _r13 = selectedRemnants;
                    HOperatorSet.ShapeTrans(selectedRemnants, out HObject selectedConvex, "convex");
                    using var _r14 = selectedConvex;
                    HOperatorSet.Union1(selectedConvex, out HObject selectedUnion);
                    using var _r15 = selectedUnion;

                    // 从"剩余 chamber"中减掉本类已处理区域，防止下个类别重复
                    HOperatorSet.Difference(chambersRemaining, selectedUnion, out HObject newChambersRemaining);
                    chambersRemaining.Dispose();
                    chambersRemaining = newChambersRemaining;

                    // 累积到最终药丸区域集
                    HOperatorSet.ConcatObj(selectedUnion, finalClasses, out HObject newFinalClasses);
                    finalClasses.Dispose();
                    finalClasses = newFinalClasses;
                }

                // ════════════════════════════════════════════════════════════
                // 第 4 步：检查正确性 —— 区分"错药"和"缺药"
                // ════════════════════════════════════════════════════════════
                // chamber 并集中未被任何类覆盖的区域 = 有问题的格子。
                // 对每个问题区域求灰度标准差（Deviation）：
                //   - Deviation > 40：该处有药丸但颜色不对 → 错药（红色叠加）
                //   - Deviation ≤ 40：该处近乎空白 → 缺药（黄色叠加）
                HOperatorSet.GenEmptyObj(out HObject missingPills);
                HOperatorSet.GenEmptyObj(out HObject wrongPills);
                HOperatorSet.Difference(_chambersUnion, finalClasses, out HObject leftOvers);
                using var _r16 = leftOvers;

                HOperatorSet.AreaCenter(leftOvers, out HTuple leftOverArea, out _, out _);
                using var _t6 = leftOverArea;
                if (leftOverArea.D > 0)
                {
                    HOperatorSet.Connection(leftOvers, out HObject leftOverConnected);
                    using var _r17 = leftOverConnected;
                    HOperatorSet.CountObj(leftOverConnected, out HTuple numProblems);
                    using var _t7 = numProblems;

                    for (int index = 1; index <= numProblems.I; index++)
                    {
                        HOperatorSet.SelectObj(leftOverConnected, out HObject problemRegion, index);
                        HOperatorSet.Intensity(problemRegion, imageB, out _, out HTuple deviation);
                        using var _t8 = deviation;

                        if (deviation.D > WrongPillDeviationThreshold)
                        {
                            HOperatorSet.ConcatObj(wrongPills, problemRegion, out HObject newWrong);
                            wrongPills.Dispose();
                            wrongPills = newWrong;
                        }
                        else
                        {
                            HOperatorSet.ConcatObj(missingPills, problemRegion, out HObject newMissing);
                            missingPills.Dispose();
                            missingPills = newMissing;
                        }
                        problemRegion.Dispose();
                    }
                }
                imageB.Dispose();

                // ════════════════════════════════════════════════════════════
                // 第 5 步：统计各类药丸数量
                // ════════════════════════════════════════════════════════════
                // FinalClasses 按类分组存放（每组一个对象），对每组连通域计数即可
                var detectedCounts = new int[PillTypeCount.Length];
                for (int i = 1; i <= PillTypeCount.Length; i++)
                {
                    HOperatorSet.SelectObj(finalClasses, out HObject classObj, i);
                    HOperatorSet.Connection(classObj, out HObject connected);
                    HOperatorSet.CountObj(connected, out HTuple size);
                    detectedCounts[i - 1] = size.I;
                    classObj.Dispose();
                    connected.Dispose();
                    size.Dispose();
                }

                // ════════════════════════════════════════════════════════════
                // 第 6 步：判定 OK/NG
                // ════════════════════════════════════════════════════════════
                HOperatorSet.CountObj(wrongPills, out HTuple wrongCount);
                HOperatorSet.CountObj(missingPills, out HTuple missingCount);
                using var _t9 = wrongCount;
                using var _t10 = missingCount;

                bool isOk = detectedCounts.SequenceEqual(PillTypeCount)
                    && wrongCount.I == 0
                    && missingCount.I == 0;

                Log.Logger.Information("泡罩药丸检测完成：IsOk={IsOk}, 各类=[{Detected}], 期望=[{Expected}], 缺药={Missing}, 错药={Wrong}",
                    isOk, string.Join(",", detectedCounts), string.Join(",", PillTypeCount), missingCount.I, wrongCount.I);

                return new BlisterCheckResult
                {
                    IsValid = true,
                    IsOk = isOk,
                    ExpectedCounts = PillTypeCount,
                    DetectedCounts = detectedCounts,
                    MissingCount = missingCount.I,
                    WrongCount = wrongCount.I,
                    DisplayImage = new HImage(imageAffineTrans),   // 对齐图（UI 显示）
                    FinalClasses = finalClasses,       // 绿色叠加（正确药丸）
                    WrongPills = wrongPills,           // 红色叠加（错药）
                    MissingPills = missingPills        // 黄色叠加（缺药）
                };
            }
            catch (Exception ex)
            {
                // R4：异常兜底，不崩进程，返回 Invalid
                Log.Logger.Error(ex, "泡罩药丸检测：检测异常");
                return BlisterCheckResult.Invalid;
            }
        }
    }

    /// <summary>
    /// 从参考图提取泡罩板轮廓、15 个 chamber 网格和 3 类药丸像素区域。
    ///
    /// 【原理】
    /// 1. 阈值 + 面积筛选提取泡罩板外轮廓，转凸包得到稳定外形
    /// 2. 按固定网格（5 行 × 3 列）生成 chamber 矩形，行间距 70、列间距 150
    /// 3. 按行范围把 chamber 分成 3 类（黄 1~145 / 红 145~270 / 绿 270~390）
    /// 4. 对每类用颜色通道提取药丸像素：
    ///    - 黄：B 通道直接阈值（黄在 B 通道偏暗）
    ///    - 红：B 通道反转后迟滞阈值（红在 B 通道反向后偏亮）
    ///    - 绿：B 通道反转后迟滞阈值（阈值略低，绿比红暗一些）
    /// 5. 与对应 chamber 类求交，精确到格子内的药丸像素
    /// </summary>
    /// <param name="image">参考图（彩色 HImage）。</param>
    /// <param name="chambers">15 个 chamber 矩形区域（训练后保存供检测用）。</param>
    /// <param name="chambersUnion">chamber 并集区域。</param>
    /// <param name="classes">3 类药丸区域（作为 GMM 训练样本）。</param>
    /// <param name="phiRef">参考图泡罩板角度（弧度，已加 180° 归一化）。</param>
    /// <param name="rowRef">参考图泡罩板中心 Row。</param>
    /// <param name="columnRef">参考图泡罩板中心 Column。</param>
    private static void ExtractPillTypes(HImage image,
        out HObject chambers, out HObject chambersUnion, out HObject classes,
        out HTuple phiRef, out HTuple rowRef, out HTuple columnRef)
    {
        // 提取泡罩板轮廓（threshold → connection → select_shape → shape_trans convex）
        HOperatorSet.Threshold(image, out HObject region, ThresholdMin, ThresholdMax);
        using var _r1 = region;
        HOperatorSet.Connection(region, out HObject connectedRegions);
        using var _r2 = connectedRegions;
        HOperatorSet.SelectShape(connectedRegions, out HObject selectedRegions, "area", "and", BlisterMinArea, BlisterMaxArea);
        using var _r3 = selectedRegions;
        HOperatorSet.ShapeTrans(selectedRegions, out HObject blister, "convex");
        using var _r4 = blister;

        // 参考图泡罩板中心（先算，作为网格平移基准）
        HOperatorSet.AreaCenter(blister, out _, out HTuple boardRow, out HTuple boardCol);

        // 生成 15 个 chamber 矩形（按相对布局坐标数组，5 行 × 3 列）
        HOperatorSet.GenEmptyRegion(out chambers);
        for (int i = 0; i < ChamberRows; i++)
        {
            double row = ChamberRowCenters[i];
            for (int j = 0; j < ChamberCols; j++)
            {
                double column = ChamberColCenters[j];
                HOperatorSet.GenRectangle2(out HObject rectangle, row, column, 0, ChamberHalfWidth, ChamberHalfHeight);
                HOperatorSet.ConcatObj(chambers, rectangle, out HObject newChambers);
                chambers.Dispose();
                chambers = newChambers;
                rectangle.Dispose();
            }
        }
        // 注意：chambers 是 out 参数，所有权转移给调用方，绝不能 using var 释放

        // 关键修正：把网格整体平移到泡罩板中心。
        // 坐标数组只描述格子的"相对布局"（间距），但网格中心可能不与泡罩板中心重合，
        // 导致格子整体偏左/偏右。计算网格中心与泡罩板中心偏移，整体平移所有格子。
        double gridCenterRow = ChamberRowCenters.Average();
        double gridCenterCol = ChamberColCenters.Average();
        double dRow = boardRow.D - gridCenterRow;
        double dCol = boardCol.D - gridCenterCol;

        HOperatorSet.MoveRegion(chambers, out HObject movedChambers, dRow, dCol);
        chambers.Dispose();
        chambers = movedChambers;

        // 泡罩板减 chamber 得外部图案（本例不用，保留算子保证与示例一致）
        HOperatorSet.Difference(blister, chambers, out HObject pattern);
        pattern.Dispose();

        // chamber 并集（检测阶段用其裁剪图像）
        HOperatorSet.Union1(chambers, out chambersUnion);
        // 注意：chambersUnion 是 out 参数，所有权转移给调用方，绝不能 using var 释放

        // 参考图泡罩板角度（加 180° 归一化，与检测阶段 Phi 处理一致）
        HOperatorSet.OrientationRegion(blister, out phiRef);
        phiRef = Math.PI + phiRef.D;

        // 参考图泡罩板中心（配准基准）
        rowRef = boardRow;
        columnRef = boardCol;

        // 按行范围把 chamber 分成 3 类，每类并成整体区域
        HOperatorSet.SelectShape(chambers, out HObject pillType1, "row", "and", PillType1RowMin, PillType1RowMax);
        HOperatorSet.Union1(pillType1, out HObject pillType1Union);
        pillType1.Dispose();
        using var _r7 = pillType1Union;

        HOperatorSet.SelectShape(chambers, out HObject pillType2, "row", "and", PillType2RowMin, PillType2RowMax);
        HOperatorSet.Union1(pillType2, out HObject pillType2Union);
        pillType2.Dispose();
        using var _r8 = pillType2Union;

        HOperatorSet.SelectShape(chambers, out HObject pillType3, "row", "and", PillType3RowMin, PillType3RowMax);
        HOperatorSet.Union1(pillType3, out HObject pillType3Union);
        pillType3.Dispose();
        using var _r9 = pillType3Union;

        // ════════════════════════════════════════════════════════════
        // 按颜色通道提取每类药丸像素
        // ════════════════════════════════════════════════════════════

        // 黄药丸（Class 1）：B 通道直接阈值
        HOperatorSet.ReduceDomain(image, pillType1Union, out HObject reduced1);
        using var _r10 = reduced1;
        HOperatorSet.Decompose3(reduced1, out HObject r1, out HObject g1, out HObject b1);
        r1.Dispose();
        g1.Dispose();
        using var _r11 = b1;
        HOperatorSet.Threshold(b1, out HObject yellowRegion, YellowBMin, YellowBMax);

        // 红药丸（Class 2）：B 通道反转后迟滞阈值（红色在反向后偏亮）
        HOperatorSet.ReduceDomain(image, pillType2Union, out HObject reduced2);
        using var _r12 = reduced2;
        HOperatorSet.Decompose3(reduced2, out HObject r2, out HObject g2, out HObject b2);
        r2.Dispose();
        g2.Dispose();
        using var _r13 = b2;
        HOperatorSet.InvertImage(b2, out HObject invert2);
        using var _r14 = invert2;
        HOperatorSet.HysteresisThreshold(invert2, out HObject redRegion, RedLowThreshold, RedHighThreshold, RedMinLength);

        // 绿药丸（Class 3）：B 通道反转后迟滞阈值（阈值略低，绿比红暗）
        HOperatorSet.ReduceDomain(image, pillType3Union, out HObject reduced3);
        using var _r15 = reduced3;
        HOperatorSet.Decompose3(reduced3, out HObject r3, out HObject g3, out HObject b3);
        r3.Dispose();
        g3.Dispose();
        using var _r16 = b3;
        HOperatorSet.InvertImage(b3, out HObject invert3);
        using var _r17 = invert3;
        HOperatorSet.HysteresisThreshold(invert3, out HObject greenRegion, GreenLowThreshold, GreenHighThreshold, GreenMinLength);

        // 颜色提取结果与对应 chamber 类求交，精确到格子内
        HOperatorSet.Intersection(yellowRegion, pillType1Union, out HObject pillType1Final);
        yellowRegion.Dispose();
        using var _r18 = pillType1Final;

        HOperatorSet.Intersection(redRegion, pillType2Union, out HObject pillType2Final);
        redRegion.Dispose();
        using var _r19 = pillType2Final;

        HOperatorSet.Intersection(greenRegion, pillType3Union, out HObject pillType3Final);
        greenRegion.Dispose();
        using var _r20 = pillType3Final;

        // 三类并成 GMM 训练样本集（classes）
        HOperatorSet.ConcatObj(pillType1Final, pillType2Final, out classes);
        HOperatorSet.ConcatObj(classes, pillType3Final, out HObject classesAll);
        classes.Dispose();
        classes = classesAll;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            try
            {
                // 释放 GMM 分类器（Halcon 原生句柄）
                if (_gmmHandle.TupleLength() > 0)
                {
                    HOperatorSet.ClearClassGmm(_gmmHandle);
                }
                _gmmHandle.Dispose();

                // 释放 Halcon 区域对象
                _chambers.Dispose();
                _chambersUnion.Dispose();

                // 释放参考几何 HTuple
                _phiRef.Dispose();
                _rowRef.Dispose();
                _columnRef.Dispose();
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "泡罩药丸检测：释放资源时异常");
            }
        }
    }
}
