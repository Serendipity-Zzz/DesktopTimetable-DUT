# 本科教务候选模型读取约定

2026-10-24 完整性补充：生产导入必须核对 get-data 选课 ID 集合与 datum.Lessons，拒绝分页/偏移/下一页、声明总数不符及重复字段。新响应失败会先使旧候选失效；无法确认范围时保留缓存。已验证来源按单批读取，不自动跟随陌生下一页地址。参见 [投影与完整性报告](../validation/projection-completeness-stage.md)。

2026-10-24：现已由 `AcademicBrowserImport` 接入本人手动导入。固定接收实际观察到的 get-data、timetable-layout、datum 路径；在内存中唯一选择符合公开模型的数组，再调用下述纯读取/映射器。学期 ID 来自实际查询参数，26 周来自源周序列，全部日期与一基周次须推导出同一首周周一。账号由学校页面明确 student/std bootstrap 或用户输入学号确认后散列区分。成功经管道交给主程序原子保存并显示，原始响应不落业务缓存。本人 205 次课程已导入，见 [实际联调](../validation/real-school-import-stage.md)。下述候选模型的历史限制不再表示没有生产调用。

完整 `timingUnits` 数值摘要可用 AcademicPeriodTableReader 转为 Domain 节次表。该读取器再次检查连续编号、合法时钟和无重叠，只读取显式选出的数组，不确定来源的校区/学期。实际12节 fixture 及来源见 [完整节次报告](../validation/complete-period-evidence-stage.md)。

依据已匿名读取的学校 [Lesson 模型](http://jxgl.dlut.edu.cn/student/static/scheduletable/js/models/lesson.js)、[Schedule 模型](http://jxgl.dlut.edu.cn/student/static/scheduletable/js/models/schedule.js) 和 [common.js](http://jxgl.dlut.edu.cn/student/static/scheduletable/js/common.js)。静态文件摘要见 ../validation/academic-public-assets.json。它们证明前端模型定义，不证明当前本人响应的完整模式、身份、学期和覆盖范围。

`AcademicScheduleReader.Read(lessonJson, scheduleJson)` 接收来源映射器明确选出的两个 JSON 数组，不接收或猜测接口地址、请求参数和结果字段路径。其结果是 AcademicScheduleBatch，不是 CurrentDataset；没有数据库写入或网络请求，也不用于当前自动同步。

| 对象 | 必需字段与读取语义 |
|---|---|
| Lesson | id 为正 Int64 JSON 整数；courseName 非空、最多 512 字符；teacherAssignmentList 为数组，其中 name 为最多 256 字符的非空文本 |
| Schedule | lessonId 必须引用同批 Lesson；date 为 yyyy-MM-dd；weekday 必须为 1—7 且与日期对应；startTime/endTime 为 HHmm 整数且同日递增；periods 为 1—24 的课时数量，不当作开始节次 |
| Schedule 可空项 | scheduleGroupId 为正整数；weekIndex 原样保留 0—60，不推断从 0 还是 1 编号；room、customPlace、personName 可缺失或为 null |
| Room | 读取 nameZh、building.nameZh、building.campus.nameZh，各自最多 512 字符；不自行拼接或猜测教室名称 |

保持各个实际日期、换教室、完全重复的源排课行及没有排课记录的 Lesson。教师显示名称按首次出现去重，仍保持多教师。空数组只表示空候选，不能自行产生 verifiedEmpty 或 TERM coverage。未知字段忽略其值，但所有对象中的重复 JSON 字段均拒绝；重复 Lesson ID 或任一排课行引用未知 Lesson 均拒绝整批，不返回前半批成功结果。

总输入不超过 5 MiB；JSON 深度最多 32；每个输入数组和教师数组最多 10000 条。非法输入只返回固定错误类型和记录序号，异常信息不包含源文本、姓名、账号或请求地址。

待现网核对：实际结果数组字段/接口、ID 数值类型、星期编码、周序号基准、未排课与非标准时间课程形态、学期日历及开发区节次表。已确认第一教学周周一、节次表、账号、学期、覆盖范围和真实字段映射前，不能将这些候选提交为当前课表。HHmm 时钟时间也不能直接当作课表节次。

## 显式上下文映射

`AcademicScheduleMapper.Map(batch, context)` 只接受调用方提供的账号、Term、Period 表、Coverage、周序号基准和来源版本；这些数据仍需真实适配器独立核实。本函数不负责或证明该核实过程。

日期据指定 Term 转为实际周次；若源 weekIndex 非空，则要求明确 ZeroBased/OneBased 并与日期计算一致。起止时间必须精确匹配节次起点/终点，periods 必须等于覆盖的节次数，允许课间休息但不做时间取整。任何一行位于 Coverage 外均拒绝整批，不将不完整响应自动扩大为全学期或悄悄剪裁。空排课仍需调用方明确 VerifiedEmpty。

同课程/排课组/星期/节次/地点的实际日期归并为离散周集合，源重复排课行去重；不同地点分开保留。Meeting ID 使用上述身份字段的 SHA-256，不包含周集合、名称或教师信息，因此改变覆盖周或教师显示信息不破坏同一安排的备注关联。课程教师信息由同批 Lesson 教师与排课 personName 合并、去重、排序，使同课程在不同安排上的元数据一致；不猜测分隔符拆分姓名。

返回 AcademicMappedBatch：已通过 Domain 校验的 Dataset，以及 LessonIdsWithoutSchedules。后者仅表示本次响应中没有排课的课程，不能在周/日响应下解释为整学期未排课。此函数没有网络/存储副作用，目前没有接入真实同步或账号绑定。
